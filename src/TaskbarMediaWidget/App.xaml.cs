using System.Windows;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Media;
using TaskbarMediaWidget.Taskbar;
using TaskbarMediaWidget.Tray;

namespace TaskbarMediaWidget;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instanceGuard;
    private MediaSessionService? _mediaSessionService;
    private TrayIconService? _trayIconService;
    private TaskbarWidgetWindow? _widgetWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled exception", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled dispatcher exception", args.Exception);
            args.Handled = true; // keep the widget alive rather than crashing on a recoverable UI error
        };

        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.TryAcquire())
        {
            AppLog.Info("Another instance is already running; exiting.");
            Shutdown();
            return;
        }

        AppLog.Info("Starting TaskbarMediaWidget.");

        _mediaSessionService = new MediaSessionService();
        _trayIconService = new TrayIconService();
        _trayIconService.ExitRequested += (_, _) => ExitApplication();

        CreateWidgetWindow();

        _mediaSessionService.Start();
    }

    private void CreateWidgetWindow()
    {
        _widgetWindow = new TaskbarWidgetWindow();
        _widgetWindow.PreviousRequested += (_, _) => _ = _mediaSessionService!.PreviousAsync();
        _widgetWindow.PlayPauseRequested += (_, _) => _ = _mediaSessionService!.PlayPauseAsync();
        _widgetWindow.NextRequested += (_, _) => _ = _mediaSessionService!.NextAsync();
        _widgetWindow.ExplorerRestarted += (_, _) => RecreateWidgetWindow();

        if (_mediaSessionService is not null)
        {
            _mediaSessionService.NowPlayingChanged += OnNowPlayingChanged;
        }

        _widgetWindow.Show();

        // Pick up whatever was already playing rather than waiting for the next SMTC event —
        // matters most right after an Explorer restart, where this is a brand-new window.
        _widgetWindow.UpdateNowPlaying(_mediaSessionService?.CurrentNowPlaying);
    }

    private void RecreateWidgetWindow()
    {
        // The old window's HWND was destroyed along with explorer.exe (it was a true WS_CHILD of
        // Shell_TrayWnd) — it cannot be reused, only replaced.
        if (_widgetWindow is not null)
        {
            _mediaSessionService!.NowPlayingChanged -= OnNowPlayingChanged;
            _widgetWindow.Close();
        }

        CreateWidgetWindow();
    }

    private void OnNowPlayingChanged(NowPlayingInfo? info) =>
        Dispatcher.Invoke(() => _widgetWindow?.UpdateNowPlaying(info));

    private void ExitApplication()
    {
        AppLog.Info("Exit requested from tray icon.");

        if (_mediaSessionService is not null)
        {
            _mediaSessionService.NowPlayingChanged -= OnNowPlayingChanged;
            _mediaSessionService.Dispose();
        }

        _widgetWindow?.Close();
        _trayIconService?.Dispose();
        _instanceGuard?.Dispose();

        Shutdown();
    }
}
