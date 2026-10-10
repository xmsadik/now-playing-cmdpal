namespace NowPlaying.Core;

/// <summary>
/// Collapses bursts of <see cref="Trigger"/> calls into a single callback, fired once the burst has been
/// quiet for the configured delay. Media sessions raise several events per track change; this keeps the UI calm.
/// </summary>
public sealed class Coalescer : IDisposable
{
    private readonly Action _action;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

    public Coalescer(Action action, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = action;
        _delay = delay;
        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Trigger()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _timer.Change(_delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            _action();
        }
        catch (Exception ex)
        {
            DiagLog.WriteLine($"Coalescer callback failed: {ex}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
