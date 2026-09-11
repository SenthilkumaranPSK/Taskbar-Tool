using System.Windows.Interop;
using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Taskbar;

/// <summary>
/// A hidden, top-level window that is never reparented, whose only job is to stay eligible to
/// receive the "TaskbarCreated" broadcast for the life of the process.
///
/// "TaskbarCreated" is posted via HWND_BROADCAST, and HWND_BROADCAST explicitly excludes child
/// windows — the moment TaskbarWidgetWindow attaches to Shell_TrayWnd (becoming a true WS_CHILD),
/// it stops being eligible to receive that message, and its own listener for it can never fire.
/// Worse, when explorer.exe dies, DestroyWindow on Shell_TrayWnd destroys the reparented child's
/// HWND too, so there is nothing left in that window to react anyway. This window stays
/// top-level and unparented specifically so something in the process survives to notice Explorer
/// coming back.
/// </summary>
internal sealed class ShellWatchdogWindow : IDisposable
{
    private readonly uint _taskbarCreatedMessage;
    private HwndSource? _hwndSource;

    public event EventHandler? TaskbarCreated;

    public ShellWatchdogWindow()
    {
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        var parameters = new HwndSourceParameters("TaskbarMediaWidget.ShellWatchdog")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)NativeMethods.WS_POPUP), // top-level, unowned — no WS_VISIBLE bit, so it never shows
            ExtendedWindowStyle = (int)NativeMethods.WS_EX_NOACTIVATE,
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreatedMessage)
        {
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwndSource is null)
        {
            return;
        }

        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
        _hwndSource = null;
    }
}
