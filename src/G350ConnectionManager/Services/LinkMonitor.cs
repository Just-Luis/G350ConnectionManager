using System.Windows.Threading;
using G350ConnectionManager.Models;

namespace G350ConnectionManager.Services;

/// <summary>
/// Periodically refreshes a <see cref="LinkSnapshot"/> in the background and
/// publishes it to subscribers. The UI only ever reads the last known snapshot,
/// so a slow or dead probe can never freeze the window.
///
/// This mirrors the cached-probe pattern used by USB_Network_Manager.sh v1.4.0.
/// </summary>
public sealed class LinkMonitor : IDisposable
{
    /// <summary>How often to refresh while the window is in the foreground.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _inFlight;
    private bool _probing;
    private bool _disposed;

    public LinkMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = Interval
        };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Latest snapshot. Never null after construction.</summary>
    public LinkSnapshot Current { get; private set; } = new()
    {
        Stages = new[] { ProbeResult.Unknown }
    };

    /// <summary>Raised on the UI thread whenever a new snapshot is available.</summary>
    public event EventHandler<LinkSnapshot>? Updated;

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    /// <summary>
    /// Runs one sweep immediately (e.g. the [Test connection] button).
    /// Concurrent calls collapse: the in-flight sweep is reused.
    /// </summary>
    public async Task<LinkSnapshot> RefreshAsync()
    {
        if (_disposed) return Current;
        if (_probing) return Current;

        _probing = true;
        try
        {
            _inFlight = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var snapshot = await LinkProbe.RunAsync(_inFlight.Token);
            Current = snapshot;
            Updated?.Invoke(this, snapshot);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            return Current;
        }
        catch
        {
            // A probe failure must never surface as an unhandled exception.
            return Current;
        }
        finally
        {
            _probing = false;
            _inFlight?.Dispose();
            _inFlight = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _inFlight?.Cancel();
        _inFlight?.Dispose();
    }
}
