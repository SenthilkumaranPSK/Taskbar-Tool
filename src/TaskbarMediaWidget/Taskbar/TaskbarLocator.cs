using System.Windows.Automation;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Taskbar;

/// <summary>
/// Resolves the primary taskbar window and its accurate client geometry/DPI. v1 only targets
/// the primary monitor's taskbar (Shell_TrayWnd) — secondary-monitor taskbars are out of scope.
///
/// <para>
/// <b>Nothing here may block the caller.</b> <see cref="GetTaskbarRect"/> runs on the UI thread
/// from the widget's ~1.3s poll tick. An earlier version resolved the "TaskbarFrame" automation
/// element inline on that tick and read <c>Current.BoundingRectangle</c> off it every time — both
/// are synchronous cross-process calls into explorer.exe, and the element lookup was bounded only
/// by a <c>Task.Wait(1000)</c>, which bounds the *lookup* but still parks the UI thread for up to
/// a second. Against a busy shell that produced exactly the periodic hitching you'd expect. The
/// UIA work now happens on a thread-pool thread and publishes into a cache; callers always get an
/// immediate answer, falling back to <c>GetWindowRect</c> (a cheap, in-process call) until the
/// refined rect arrives.
/// </para>
/// </summary>
internal static class TaskbarLocator
{
    private static readonly object CacheLock = new();

    private static AutomationElement? _cachedTaskbarFrame;
    private static IntPtr _cachedForHwnd;
    private static NativeMethods.RECT? _refinedRect;
    private static IntPtr _refinedForHwnd;
    private static int _refreshInFlight;

    public static IntPtr FindTaskbarHandle() => NativeMethods.FindWindow("Shell_TrayWnd", null);

    public static double GetDpiScale(IntPtr taskbarHandle)
    {
        var dpi = NativeMethods.GetDpiForWindow(taskbarHandle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// Returns the taskbar's client-area rect in screen coordinates, without ever blocking.
    /// Prefers the cached "TaskbarFrame" automation bounds (GetWindowRect on Shell_TrayWnd itself
    /// can include invisible margins on some Windows builds) and falls back to GetWindowRect until
    /// the background refresh has produced one.
    /// </summary>
    public static NativeMethods.RECT GetTaskbarRect(IntPtr taskbarHandle)
    {
        lock (CacheLock)
        {
            if (_refinedRect is { } cached && _refinedForHwnd == taskbarHandle)
            {
                return cached;
            }
        }

        BeginRefreshRect(taskbarHandle);

        NativeMethods.GetWindowRect(taskbarHandle, out var rect);
        return rect;
    }

    /// <summary>
    /// Drops the cached rect so the next <see cref="GetTaskbarRect"/> re-resolves it in the
    /// background. Call this on the events that can actually change taskbar geometry — DPI change,
    /// display change, work-area change, and re-attaching to a new taskbar HWND — rather than
    /// re-reading it on every poll tick, which is what made this expensive in the first place.
    /// </summary>
    public static void InvalidateRectCache()
    {
        lock (CacheLock)
        {
            _refinedRect = null;
            _cachedTaskbarFrame = null;
            _cachedForHwnd = IntPtr.Zero;
        }
    }

    private static void BeginRefreshRect(IntPtr taskbarHandle)
    {
        // One refresh at a time; poll ticks arrive far faster than a slow UIA round-trip completes,
        // and queueing one per tick against an unresponsive shell would pile up thread-pool work.
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                RefreshRect(taskbarHandle);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Background taskbar rect refresh failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _refreshInFlight, 0);
            }
        });
    }

    /// <summary>Runs entirely on a thread-pool thread — every call in here can block.</summary>
    private static void RefreshRect(IntPtr taskbarHandle)
    {
        var frame = TryGetCachedOrFreshTaskbarFrame(taskbarHandle);
        if (frame is null)
        {
            return;
        }

        try
        {
            var bounds = frame.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            var rect = new NativeMethods.RECT
            {
                Left = (int)bounds.Left,
                Top = (int)bounds.Top,
                Right = (int)bounds.Right,
                Bottom = (int)bounds.Bottom,
            };

            lock (CacheLock)
            {
                _refinedRect = rect;
                _refinedForHwnd = taskbarHandle;
            }
        }
        catch (ElementNotAvailableException)
        {
            // The element outlived its window (Explorer restart mid-flight) — drop it so the next
            // pass re-resolves from scratch instead of reusing a dead reference.
            lock (CacheLock)
            {
                _cachedTaskbarFrame = null;
                _cachedForHwnd = IntPtr.Zero;
            }
        }
    }

    private static AutomationElement? TryGetCachedOrFreshTaskbarFrame(IntPtr taskbarHandle)
    {
        AutomationElement? cached;
        lock (CacheLock)
        {
            cached = _cachedForHwnd == taskbarHandle ? _cachedTaskbarFrame : null;
        }

        if (cached is not null)
        {
            try
            {
                _ = cached.Current.BoundingRectangle; // touch it to force a staleness check
                return cached;
            }
            catch (ElementNotAvailableException)
            {
                lock (CacheLock)
                {
                    _cachedTaskbarFrame = null;
                    _cachedForHwnd = IntPtr.Zero;
                }
            }
        }

        var found = AutomationLookup.TryFindByAutomationId(taskbarHandle, "TaskbarFrame", timeoutMs: 1000);
        if (found is not null)
        {
            lock (CacheLock)
            {
                _cachedTaskbarFrame = found;
                _cachedForHwnd = taskbarHandle;
            }
        }

        return found;
    }
}
