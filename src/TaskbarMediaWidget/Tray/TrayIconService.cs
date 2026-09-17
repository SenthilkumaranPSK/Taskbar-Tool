using System.Drawing;
using System.Windows.Forms;
using TaskbarMediaWidget.Core;

namespace TaskbarMediaWidget.Tray;

/// <summary>
/// The only visible chrome besides the embedded widget itself: a system tray icon carrying the
/// "Run at startup" toggle and an "Exit" item, since the app otherwise has no window to close from.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Icon? _ownedIcon;

    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _startupItem = new ToolStripMenuItem("Run at startup");
        _startupItem.Click += OnToggleStartup;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        // Re-read the registry each time the menu opens rather than caching at construction —
        // the Run entry can also be flipped from Task Manager's Startup tab or Settings, and a
        // tray tick that disagrees with the real state is worse than no tick at all.
        menu.Opening += (_, _) => _startupItem.Checked = StartupRegistration.IsEnabled();

        _ownedIcon = TryLoadAppIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "Taskbar Media Widget",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        var enable = !StartupRegistration.IsEnabled();
        if (StartupRegistration.TrySetEnabled(enable))
        {
            _startupItem.Checked = enable;
            return;
        }

        // Leave the tick showing the real (unchanged) state rather than the requested one.
        _startupItem.Checked = StartupRegistration.IsEnabled();
        _notifyIcon.ShowBalloonTip(
            3000,
            "Taskbar Media Widget",
            "Couldn't update the startup setting. See the log for details.",
            ToolTipIcon.Warning);
    }

    /// <summary>
    /// Loads the multi-size app icon out of the WPF resource stream, asking for the shell's
    /// current small-icon size so the tray gets a crisply-rendered variant rather than a
    /// downscaled 32px one (which is what Icon.ExtractAssociatedIcon would hand back).
    /// </summary>
    private static Icon? TryLoadAppIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute));

            if (resource?.Stream is not { } stream)
            {
                return null;
            }

            using (stream)
            {
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to load tray icon, falling back to the system icon: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
    }
}
