using System.Diagnostics;
using System.IO;
using Renci.SshNet;

namespace G350ConnectionManager.Services;

/// <summary>
/// Records the console's screen to a video file.
///
/// Frames are pulled with the same <see cref="ScreenCaptureService"/> the preview
/// uses and piped into ffmpeg on this PC, which encodes H.264 into MP4.
///
/// Two deliberate choices:
///
/// 1. Encoding happens here, never on the console. ffmpeg on the device measured
///    ~1.0-1.5 s per frame on four weak cores, which would ruin whatever game is
///    being recorded.
///
/// 2. MP4/H.264 rather than the hand-rolled MJPEG AVI the plan originally called
///    for. ffmpeg is already present, H.264 is several times smaller at the same
///    visual quality, and the result plays in every player and editor without
///    conversion. If ffmpeg is missing the recorder reports that plainly instead
///    of writing a file nothing can open.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private Process? _ffmpeg;
    private ScreenCaptureService? _capture;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private string? _path;

    public bool IsRecording => _worker is not null && !_worker.IsCompleted;

    public string? CurrentPath => _path;

    /// <summary>Frames written so far, for the timer badge.</summary>
    public long Frames { get; private set; }

    /// <summary>Why the last start failed, if it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>True when ffmpeg can be found and run.</summary>
    public static bool FfmpegAvailable()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-hide_banner -version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (probe is null) return false;
            probe.WaitForExit(5000);
            return probe.HasExited && probe.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts recording. <paramref name="settings"/> controls resolution and
    /// frame rate; JPEG quality is irrelevant here because the frames go
    /// straight into the encoder as pixels, not as JPEG.
    /// </summary>
    public bool Start(string host, CaptureSettings settings, string folder,
                      int crf = 23)
    {
        Stop();

        LastError = null;
        Frames = 0;

        if (!FfmpegAvailable())
        {
            LastError = "ffmpeg was not found on this PC, so recording is unavailable. "
                      + "Install it and make sure it is on PATH.";
            return false;
        }

        Directory.CreateDirectory(folder);

        // Resolution and frame rate are fixed for the whole file: an encoder
        // cannot change dimensions mid-stream, and a variable frame rate would
        // make the timestamps wrong.
        var width = settings.Width;
        var height = settings.Height;
        var fps = Math.Max(1, (int)Math.Round(settings.EffectiveFps));

        var name = $"g350-{DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4";
        _path = Path.Combine(folder, name);

        try
        {
            _ffmpeg = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments =
                    "-hide_banner -loglevel error -y " +
                    "-f rawvideo -pix_fmt bgra " +
                    $"-s {width}x{height} -r {fps} -i - " +
                    "-c:v libx264 -preset veryfast " +
                    $"-crf {Math.Clamp(crf, 0, 51)} " +
                    // 4:2:0 needs even dimensions; CaptureSettings guarantees them.
                    "-pix_fmt yuv420p -movflags +faststart " +
                    $"\"{_path}\"",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (_ffmpeg is null)
            {
                LastError = "ffmpeg could not be started.";
                return false;
            }

            // Capture errors so a failed encode can be explained rather than
            // leaving behind a 0-byte file.
            _ffmpeg.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LastError = e.Data;
            };
            _ffmpeg.BeginErrorReadLine();

            _capture = new ScreenCaptureService();
            if (!_capture.StartAsync(host, fps * 2).GetAwaiter().GetResult())
            {
                LastError = _capture.LastError ?? "Could not start the capture stream.";
                Stop();
                return false;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var stdin = _ffmpeg.StandardInput.BaseStream;

            _worker = Task.Run(() => RecordLoop(stdin, width, height, fps, token), token);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
            return false;
        }
    }

    /// <summary>
    /// Pulls frames and writes them to ffmpeg, duplicating as needed so the file
    /// stays at a constant frame rate even when the link cannot keep up.
    ///
    /// A constant rate matters: ffmpeg timestamps frames by position, so writing
    /// fewer frames than the stated fps would silently speed the video up.
    /// </summary>
    private async Task RecordLoop(Stream stdin, int width, int height, int fps,
                                  CancellationToken token)
    {
        var capture = _capture;
        if (capture is null) return;

        var frameInterval = TimeSpan.FromSeconds(1.0 / fps);
        var clock = Stopwatch.StartNew();
        var nextDue = TimeSpan.Zero;
        byte[]? lastPixels = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = capture.ReadFrame(token);
                if (frame is null) break;

                var scaled = FrameConverter.ToRawBgra(frame, width, height);

                // Hold the newest frame so gaps repeat it rather than stalling.
                lastPixels = scaled;

                while (nextDue <= clock.Elapsed)
                {
                    await stdin.WriteAsync(scaled, token);
                    Frames++;
                    nextDue += frameInterval;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            // Pad any trailing gap so the container length matches the wall
            // clock instead of ending early.
            if (lastPixels is not null)
            {
                try
                {
                    while (nextDue <= clock.Elapsed && Frames < int.MaxValue)
                    {
                        await stdin.WriteAsync(lastPixels, CancellationToken.None);
                        Frames++;
                        nextDue += frameInterval;
                    }
                }
                catch
                {
                    // Pipe already closed by Stop().
                }
            }

            try { await stdin.FlushAsync(CancellationToken.None); } catch { }
        }
    }

    /// <summary>Stops recording and finalises the file.</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }

        // Closing ffmpeg's input is what makes it write the container trailer;
        // killing the process first leaves an unplayable file.
        try { _ffmpeg?.StandardInput.Close(); } catch { }

        try { _worker?.Wait(TimeSpan.FromSeconds(10)); } catch { }

        try
        {
            if (_ffmpeg is not null && !_ffmpeg.HasExited)
                _ffmpeg.WaitForExit(15000);
        }
        catch { }

        try { _capture?.Stop(); } catch { }

        var host = _capture is not null ? App.Link.Current.ConsoleIp : null;
        if (!string.IsNullOrEmpty(host))
        {
            var h = host;
            Task.Run(() => ScreenCaptureService.KillRemote(h));
        }

        try { _ffmpeg?.Dispose(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _ffmpeg = null;
        _capture = null;
        _worker = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
