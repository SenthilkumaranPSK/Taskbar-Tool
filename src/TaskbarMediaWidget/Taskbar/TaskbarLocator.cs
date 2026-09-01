using System.Windows.Automation;
using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Taskbar;

/// <summary>
/// Resolves the primary taskbar window and its accurate client geometry/DPI. v1 only targets
/// the primary monitor's taskbar (Shell_TrayWnd) — secondary-monitor taskbars are out of scope.
/// </summary>
internal static class TaskbarLocator
{
    private static AutomationElement? _cachedTaskbarFrame;
    private static IntPtr _cachedForHwnd;

    public static IntPtr FindTaskbarHandle() => NativeMethods.FindWindow("Shell_TrayWnd", null);

    public static double GetDpiScale(IntPtr taskbarHandle)
    {
        var dpi = NativeMethods.GetDpiForWindow(taskbarHandle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// Returns the taskbar's client-area rect in screen coordinates. Prefers the "TaskbarFrame"
    /// automation element's bounding rect (GetWindowRect on Shell_TrayWnd itself can include
    /// invisible margins on some Windows builds); falls back to GetWindowRect if automation
    /// fails or times out.
    /// </summary>
    public static NativeMethods.RECT GetTaskbarRect(IntPtr taskbarHandle)
    {
        var frame = TryGetCachedOrFreshTaskbarFrame(taskbarHandle);
        if (frame != null)
        {
            try
            {
                var bounds = frame.Current.BoundingRectangle;
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                {
                    return new NativeMethods.RECT
                    {
                        Left = (int)bounds.Left,
                        Top = (int)bounds.Top,
                        Right = (int)bounds.Right,
                        Bottom = (int)bounds.Bottom,
                    };
                }
            }
            catch (ElementNotAvailableException)
            {
                _cachedTaskbarFrame = null;
            }
        }

        NativeMethods.GetWindowRect(taskbarHandle, out var rect);
        return rect;
    }

    private static AutomationElement? TryGetCachedOrFreshTaskbarFrame(IntPtr taskbarHandle)
    {
        if (_cachedTaskbarFrame != null && _cachedForHwnd == taskbarHandle)
        {
            try
            {
                _ = _cachedTaskbarFrame.Current.BoundingRectangle; // touch it to force staleness check
                return _cachedTaskbarFrame;
            }
            catch (ElementNotAvailableException)
            {
                _cachedTaskbarFrame = null;
            }
        }

        var found = AutomationLookup.TryFindByAutomationId(taskbarHandle, "TaskbarFrame", timeoutMs: 1000);
        if (found != null)
        {
            _cachedTaskbarFrame = found;
            _cachedForHwnd = taskbarHandle;
        }

        return found;
    }
}
