namespace TaskbarMediaWidget.Core;

/// <summary>
/// Prevents two copies of the widget from embedding themselves into the taskbar at once.
/// Uses a "Global\" prefixed mutex name so the guard holds across user sessions/elevation
/// levels, not just within the current one.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Global\TaskbarMediaWidget_SingleInstance";

    private readonly Mutex _mutex;
    private bool _owned;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);
    }

    /// <summary>True if this process is the only instance and now owns the guard.</summary>
    public bool TryAcquire()
    {
        try
        {
            _owned = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance crashed while holding it — we still get ownership.
            _owned = true;
        }

        return _owned;
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
