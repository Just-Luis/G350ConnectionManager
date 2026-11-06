using System.Collections.Concurrent;
using Renci.SshNet;

namespace G350ConnectionManager.Services;

/// <summary>Where a line of console output came from.</summary>
public enum ConsoleLineKind
{
    /// <summary>The command the user ran, echoed back.</summary>
    Command,
    Output,
    Error,
    /// <summary>Something the app reports, e.g. a cancelled command.</summary>
    Notice
}

public sealed record ConsoleLine(ConsoleLineKind Kind, string Text);

/// <summary>
/// Runs shell commands on the console and collects their output.
///
/// Each command gets its own SSH session and channel, so a hung or long-running
/// command cannot block the next one, and closing the session cancels the remote
/// process.
///
/// Output is queued and drained by the UI rather than raised as events from a
/// background thread: WPF will not accept a collection changed off its dispatcher.
/// </summary>
public sealed class RemoteConsoleService : IDisposable
{
    private const string User = "ark";
    private const string Password = "ark";

    private readonly ConcurrentQueue<ConsoleLine> _pending = new();
    private SshClient? _active;
    private CancellationTokenSource? _cts;

    /// <summary>True while a command is running.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Why the last command could not start, if it did not.</summary>
    public string? LastError { get; private set; }

    private void Queue(ConsoleLineKind kind, string text) =>
        _pending.Enqueue(new ConsoleLine(kind, text));

    /// <summary>Takes everything queued since the previous call.</summary>
    public List<ConsoleLine> DrainPending()
    {
        var result = new List<ConsoleLine>();
        while (_pending.TryDequeue(out var line)) result.Add(line);
        return result;
    }

    public Task RunAsync(string host, string command) => Task.Run(() => Run(host, command));

    private async Task Run(string host, string command)
    {
        LastError = null;
        IsBusy = true;
        Queue(ConsoleLineKind.Command, command);

        try
        {
            var client = new SshClient(host, User, Password)
            {
                ConnectionInfo = { Timeout = TimeSpan.FromSeconds(8) }
            };

            client.Connect();
            using var _ = client;

            var cmd = client.CreateCommand(command);

            // Long but finite: TimeSpan.Zero means "infinite" and trips an
            // internal guard. Cancellation is the real stop path.
            cmd.CommandTimeout = TimeSpan.FromHours(1);

            _cts = new CancellationTokenSource();
            _active = client;

            // ExecuteAsync(CancellationToken) is the interruptible API; the
            // Begin*/End* pair has no cancellation and would leave a hung
            // command running on the device.
            //
            // Output is read from Result/Error once it finishes. OutputStream is
            // only populated after completion, so reading it mid-flight returns
            // nothing.
            try
            {
                await cmd.ExecuteAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Queue(ConsoleLineKind.Notice, "stopped");
            }
            catch (Exception ex)
            {
                Queue(ConsoleLineKind.Error, ex.Message);
            }

            Emit(cmd.Result, ConsoleLineKind.Output);
            Emit(cmd.Error, ConsoleLineKind.Error);

            if (!_cts.IsCancellationRequested && cmd.ExitStatus is { } exit)
                Queue(ConsoleLineKind.Notice, $"exit {exit}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Queue(ConsoleLineKind.Error, ex.Message);
        }
        finally
        {
            _active = null;
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    /// <summary>Queues each non-empty line of output.</summary>
    private void Emit(string? text, ConsoleLineKind kind)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > 0) Queue(kind, line);
        }
    }

    /// <summary>
    /// Stops the running command.
    ///
    /// Cancelling the token aborts ExecuteAsync, and disconnecting the session
    /// closes the channel so the remote process actually goes away instead of
    /// continuing to run unobserved.
    /// </summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _active?.Disconnect(); } catch { }
    }

    public void Dispose() => Stop();
}
