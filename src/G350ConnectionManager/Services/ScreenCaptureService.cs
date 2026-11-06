using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Renci.SshNet;

namespace G350ConnectionManager.Services;

/// <summary>A single captured frame: BGRA pixels, tightly packed per row.</summary>
public sealed class CapturedFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Pixels { get; init; }

    public int Stride => Width * 4;
}

/// <summary>
/// Streams frames from the console's screen.
///
/// The console-side grabber is uploaded to /tmp on every session and started
/// through SSH, so nothing is permanently installed on the device — a permanent
/// copy would silently drift out of sync with the app's protocol.
///
/// The grabber writes a length-prefixed stream:
///
///     G350FRAME &lt;width&gt; &lt;height&gt; &lt;nbytes&gt;\n
///     &lt;nbytes of BGRA&gt;
///
/// Framing matters: the previous tool read a fixed 640x480x4 block, which
/// silently desynchronises if the capture ever returns a different size.
///
/// Capture does not need root — /dev/fb0 and /dev/dri/card0 are group-readable
/// by `video`, and `ark` is in that group.
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    private const string User = "ark";
    private const string Password = "ark";
    private const string RemotePath = "/tmp/g350_grabber.py";

    private SshClient? _client;
    private SshCommand? _command;
    private Task? _pump;
    private BlockingByteQueue? _queue;
    private Queue<byte> _pending = new();
    private string? _host;

    /// <summary>Frames decoded so far, for the UI's counter.</summary>
    public int FrameCount { get; private set; }

    public bool IsRunning => _queue is not null;

    /// <summary>
    /// Starts streaming. <paramref name="fps"/> is a target, not a guarantee —
    /// each frame is a full 1.2 MB capture plus transfer.
    /// </summary>
    public Task<bool> StartAsync(string host, double fps = 10)
    {
        return Task.Run(() => Start(host, fps, once: false));
    }

    /// <summary>Starts a one-shot capture (screenshots).</summary>
    public bool StartOnce(string host)
    {
        return Start(host, 1, once: true);
    }

    private bool Start(string host, double fps, bool once)
    {
        Stop();

        try
        {
            var client = new SshClient(host, User, Password);
            client.Connect();

            // Upload the grabber each session (a few KB, negligible) so the
            // device never holds a stale copy of the protocol.
            using (var sftp = new SftpClient(host, User, Password))
            {
                sftp.Connect();
                using var source = typeof(ScreenCaptureService).Assembly
                    .GetManifestResourceStream("G350ConnectionManager.g350_grabber.py")
                    ?? throw new InvalidOperationException("The bundled screen grabber is missing.");

                sftp.UploadFile(source, RemotePath, true);
                sftp.Disconnect();
            }

            var args = once
                ? "--once"
                : $"--fps {fps.ToString("0.##", CultureInfo.InvariantCulture)}";

            // Elevated on purpose. DRM buffer export needs privileges on this
            // device: as 'ark', drmModeGetFB returns handle=0 and
            // drmPrimeHandleToFD fails, so no GPU buffer can be read. The same
            // call as root returns a valid handle and the full frame. Emulation
            // Station and RetroArch render through DRM, so without this the
            // preview is blank whenever ES or a game is on screen.
            //
            // The password is piped into `sudo -S`, matching the console's own
            // scripts, so it never appears in the process list as an argument.
            //
            // Streamed straight back, with no PTY. An exec channel has no
            // terminal, so nothing echoes the command into the byte stream.
            // (A ShellStream does allocate a PTY and injects bracketed-paste
            // escapes, which corrupts frames.)
            //
            // Do NOT redirect to a file and cat it: the grabber streams
            // continuously and never exits, so `cat` would never run and the
            // reader would sit on an empty file.
            var command =
                $"echo {Password} | sudo -S -p '' python3 {RemotePath} {args}";

            var cmd = client.CreateCommand(command);
            // Long but finite: TimeSpan.Zero means "infinite" and trips an
            // internal guard in SSH.NET. A stream is stopped explicitly by
            // Stop(), not by a timeout.
            cmd.CommandTimeout = TimeSpan.FromHours(6);

            // SSH.NET quirk: SshCommand.CreateInputStream() throws unless the
            // command is *already* executing, and Execute() does not return
            // until it finishes. So execution is kicked off on its own task,
            // CreateInputStream() is retried until it succeeds, and the bytes
            // are copied into an in-memory pipe owned by this class.
            //
            // The pipe is what makes the output readable after Execute()
            // finishes, and it is bounded so a stalled reader applies
            // back-pressure to the console instead of buffering without limit.
            // A simple thread-safe queue of byte chunks. System.IO.Pipelines'
            // AsStream() does not support Read(), and an unbounded queue is fine
            // here: ReadFrame() consumes a frame within milliseconds, and Stop()
            // clears whatever is left.
            var queue = new BlockingByteQueue();

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var pump = Task.Run(async () =>
            {
                // Use the raw SSH channel rather than SshCommand.CreateInputStream():
                // that helper is only valid while Execute() runs and throws
                // "Specified method is not supported" on this SSH.NET build.
                // An exec channel gives a direct pipe from the remote process,
                // with no PTY and therefore no echo or bracketed-paste noise.
                try
                {
                    // SshCommand under the hood, but reading its channel directly.
                    var cmd2 = client.CreateCommand(command);
                    cmd2.CommandTimeout = TimeSpan.FromHours(6);

                    var exec = Task.Run(() =>
                    {
                        try { cmd2.Execute(); }
                        catch (Exception ex) { failure.TrySetResult(ex); }
                    });

                    // The channel is available once execution has begun.
                    System.IO.Stream src = null!;
                    for (var attempt = 0; attempt < 100; attempt++)
                    {
                        try
                        {
                            src = cmd2.OutputStream;
                            if (src is not null) break;
                        }
                        catch { }
                        await Task.Delay(50);
                    }

                    if (src is null)
                    {
                        LastError = "the console never produced a capture stream";
                        ready.TrySetResult(false);
                        queue.Complete();
                        return;
                    }

                    ready.TrySetResult(true);

                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var n = src.Read(buffer, 0, buffer.Length);
                        if (n <= 0) { LastError = "the capture stream closed"; break; }

                        var chunk = new byte[n];
                        Buffer.BlockCopy(buffer, 0, chunk, 0, n);
                        queue.Add(chunk);
                    }

                    try { await exec; } catch { }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    failure.TrySetResult(ex);
                }
                finally
                {
                    queue.Complete();
                }
            });

            var holder = Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(20)))
                             .GetAwaiter().GetResult();
            if (holder != ready.Task || !ready.Task.Result)
            {
                var err = failure.Task.IsCompletedSuccessfully ? failure.Task.Result : null;
                LastError = err?.Message ?? LastError ?? "the console did not start the capture";
                throw new InvalidOperationException(LastError);
            }

            _queue = queue;
            _pending = new Queue<byte>();
            _pump = pump;

            _client = client;
            _command = cmd;
            _pump = pump;
            _host = host;
            FrameCount = 0;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Stop();
            return false;
        }
    }

    /// <summary>Why the last StartAsync failed.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Reads the next frame, blocking until one arrives.
    /// Returns null when the stream ends or is stopped.
    /// </summary>
    public CapturedFrame? ReadFrame(CancellationToken token)
    {
        var queue = _queue;
        if (queue is null) return null;

        try
        {
            // Resynchronise on the header. Anything before it (sudo chatter, a
            // stray notice) is discarded rather than misread as pixels.
            for (var guard = 0; guard < 20; guard++)
            {
                var line = ReadLine(queue, token);
                if (line is null) return null;
                if (!line.StartsWith("G350FRAME ", StringComparison.Ordinal)) continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) return null;

                if (!int.TryParse(parts[1], out var width) ||
                    !int.TryParse(parts[2], out var height) ||
                    !int.TryParse(parts[3], out var bytes))
                    return null;

                if (width <= 0 || height <= 0) return null;

                // Guard against a corrupt length turning into a huge allocation.
                if (bytes <= 0 || bytes > width * height * 4) return null;

                var pixels = ReadExactly(queue, bytes, token);
                if (pixels is null) return null;

                FrameCount++;
                return new CapturedFrame { Width = width, Height = height, Pixels = pixels };
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads one newline-terminated header line.</summary>
    private string? ReadLine(BlockingByteQueue queue, CancellationToken token)
    {
        var sb = new StringBuilder(32);

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var b = queue.Read(token);
            if (b < 0) return null;
            if (b == 10) return sb.ToString();
            if (sb.Length < 256) sb.Append((char)b);
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes of pixel data.</summary>
    private static byte[]? ReadExactly(BlockingByteQueue queue, int count, CancellationToken token)
    {
        var result = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            token.ThrowIfCancellationRequested();

            var b = queue.Read(token);
            if (b < 0) return null;
            result[offset++] = (byte)b;
        }

        return result;
    }

    public void Stop()
    {

        try { _command?.CancelAsync(); } catch { }
        try { _command?.Dispose(); } catch { }
        try { _client?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }

        _queue = null;
        _pending.Clear();
        _command = null;
        _pump = null;
        _client = null;
        _host = null;
    }

    /// <summary>Kills any grabber left running on the console.</summary>
    public static void KillRemote(string host)
    {
        try
        {
            using var client = new SshClient(host, User, Password);
            client.Connect();
            using var cmd = client.CreateCommand("pkill -f g350_grabber.py");
            cmd.CommandTimeout = TimeSpan.FromSeconds(5);
            cmd.Execute();
            client.Disconnect();
        }
        catch
        {
            // Nothing to clean up, or the console is gone.
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Blocking byte queue fed by the SSH reader thread and drained by ReadFrame().
///
/// A plain lock + monitor is used deliberately. System.IO.Pipelines' AsStream()
/// reports "Specified method is not supported" for Read, and awaiting
/// ChannelReader.WaitToReadAsync() from the reader thread was likewise
/// unsupported, so neither fits a simple synchronous consumer.
/// </summary>
internal sealed class BlockingByteQueue
{
    private readonly object _gate = new();
    private readonly Queue<byte> _bytes = new();
    private bool _completed;

    public void Add(byte[] chunk)
    {
        lock (_gate)
        {
            foreach (var b in chunk) _bytes.Enqueue(b);
            Monitor.PulseAll(_gate);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Next byte, or -1 when the stream is finished.</summary>
    public int Read(CancellationToken token)
    {
        lock (_gate)
        {
            while (_bytes.Count == 0 && !_completed)
            {
                token.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 100);
            }

            if (_bytes.Count == 0) return -1;
            return _bytes.Dequeue();
        }
    }
}
