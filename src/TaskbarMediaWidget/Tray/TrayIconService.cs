using System.Drawing;
using System.Windows.Forms;

namespace TaskbarMediaWidget.Tray;

/// <summary>
/// The only visible chrome besides the embedded widget itself: a system tray icon with a
/// right-click "Exit" item, since the app otherwise has no window to close from.
///
/// Uses the stock application icon as a placeholder — swap NotifyIcon.Icon for a real .ico
/// (e.g. via Icon.ExtractAssociatedIcon or an embedded resource) once one exists.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Taskbar Media Widget",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
