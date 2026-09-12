namespace TaskbarMediaWidget.Core;

/// <summary>
/// Prevents two copies of the widget from embedding themselves into the taskbar at once. Uses a
/// "Local\" prefixed mutex name, scoped to the current session — every logged-on user gets their
/// own explorer.exe and their own taskbar, so every user should get their own widget instance
/// rather than one session's guard blocking another's (which a "Global\" prefix would do under
/// fast user switching or RDP).
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\TaskbarMediaWidget_SingleInstance";

    private Mutex? _mutex;
    private bool _owned;

    /// <summary>True if this process is the only instance and now owns the guard.</summary>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName);
            _owned = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance crashed while holding it — we still get ownership.
            _owned = true;
        }
        catch (Exception)
        {
            // Constructing/acquiring the mutex can itself fail (e.g. UnauthorizedAccessException
            // if a same-named object already exists under a different ACL) — treat that as "not
            // the first instance" rather than crashing the app at startup over a guard check.
            _owned = false;
        }

        return _owned;
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex?.ReleaseMutex();
        }

        _mutex?.Dispose();
    }
}
