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
    private ShellStream? _shell;
    private Stream? _stream;
    private string? _host;

    /// <summary>Frames decoded so far, for the UI's counter.</summary>
    public int FrameCount { get; private set; }

    public bool IsRunning => _stream is not null;

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

            // A ShellStream is used rather than SshCommand.CreateInputStream()
            // because that stream is only valid while the command itself is
            // executing, which does not work for a long-lived producer.
            var shell = client.CreateShellStream(
                "g350capture", 200, 50, 640, 480, 1 << 20);

            var args = once
                ? "--once"
                : $"--fps {fps.ToString("0.##", CultureInfo.InvariantCulture)}";
            shell.WriteLine($"python3 {RemotePath} {args}");

            _client = client;
            _shell = shell;
            _stream = shell;
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
        var stream = _stream;
        if (stream is null) return null;

        try
        {
            var header = ReadLine(stream, token);
            if (header is null) return null;

            // Skip anything that is not our header (banner/ssh noise).
            var guard = 0;
            while (!header.StartsWith("G350FRAME ", StringComparison.Ordinal))
            {
                if (++guard > 20) return null;
                header = ReadLine(stream, token);
                if (header is null) return null;
            }

            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return null;

            if (!int.TryParse(parts[1], out var width) ||
                !int.TryParse(parts[2], out var height) ||
                !int.TryParse(parts[3], out var bytes))
                return null;

            if (width <= 0 || height <= 0) return null;

            // Guard against a corrupt length turning into a huge allocation.
            if (bytes <= 0 || bytes > width * height * 4) return null;

            var buffer = new byte[bytes];
            var read = 0;
            while (read < bytes)
            {
                token.ThrowIfCancellationRequested();
                var n = stream.Read(buffer, read, bytes - read);
                if (n <= 0) return null;
                read += n;
            }

            FrameCount++;
            return new CapturedFrame { Width = width, Height = height, Pixels = buffer };
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

    private static string? ReadLine(Stream stream, CancellationToken token)
    {
        var sb = new StringBuilder(32);

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var b = stream.ReadByte();
            if (b < 0) return null;
            if (b == '\n') return sb.ToString();
            if (sb.Length < 256) sb.Append((char)b);
        }
    }

    public void Stop()
    {
        try { _shell?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }

        _shell = null;
        _stream = null;
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
