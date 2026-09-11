using System.Windows;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Interop;
using TaskbarMediaWidget.Media;
using TaskbarMediaWidget.Taskbar;
using TaskbarMediaWidget.Tray;

namespace TaskbarMediaWidget;

public partial class App : System.Windows.Application
{
    private const int ExplorerReadyPollIntervalMs = 200;
    private const int ExplorerReadyTimeoutMs = 60_000;

    private SingleInstanceGuard? _instanceGuard;
    private MediaSessionService? _mediaSessionService;
    private TrayIconService? _trayIconService;
    private TaskbarWidgetWindow? _widgetWindow;
    private ShellWatchdogWindow? _shellWatchdog;
    private bool _explorerRestarting;

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

        // Never reparented, so — unlike TaskbarWidgetWindow — it stays eligible to receive
        // "TaskbarCreated" for the life of the process. See ShellWatchdogWindow for why.
        _shellWatchdog = new ShellWatchdogWindow();
        _shellWatchdog.TaskbarCreated += (_, _) => _ = HandleExplorerRestartAsync();

        CreateWidgetWindow();

        _mediaSessionService.Start();
    }

    private void CreateWidgetWindow()
    {
        _widgetWindow = new TaskbarWidgetWindow();
        _widgetWindow.PreviousRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.PreviousAsync());
        _widgetWindow.PlayPauseRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.PlayPauseAsync());
        _widgetWindow.NextRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.NextAsync());

        if (_mediaSessionService is not null)
        {
            _mediaSessionService.NowPlayingChanged += OnNowPlayingChanged;
        }

        _widgetWindow.Show();

        // Pick up whatever was already playing rather than waiting for the next SMTC event —
        // matters most right after an Explorer restart, where this is a brand-new window.
        _widgetWindow.UpdateNowPlaying(_mediaSessionService?.CurrentNowPlaying);
    }

    // Transport-control clicks were previously fired with "_ = ...Async()" directly — any fault
    // (e.g. the SMTC session collection being mutated mid-enumeration) landed on an unobserved
    // Task and vanished silently, so the button did nothing with no log line. Route through here
    // instead so failures are at least visible in AppLog.
    private static async Task RunCommandAsync(Task command)
    {
        try
        {
            await command;
        }
        catch (Exception ex)
        {
            AppLog.Error("Media transport command failed", ex);
        }
    }

    private async Task HandleExplorerRestartAsync()
    {
        if (_explorerRestarting)
        {
            return;
        }

        _explorerRestarting = true;
        AppLog.Info("Detected Explorer restart; waiting for taskbar to come back.");

        var waited = 0;
        while (waited < ExplorerReadyTimeoutMs)
        {
            var handle = TaskbarLocator.FindTaskbarHandle();
            if (handle != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out var rect) && rect.Width > 0 && rect.Height > 0)
            {
                break;
            }

            await Task.Delay(ExplorerReadyPollIntervalMs);
            waited += ExplorerReadyPollIntervalMs;
        }

        AppLog.Info("Taskbar is back; recreating widget window.");
        _explorerRestarting = false;
        Dispatcher.Invoke(RecreateWidgetWindow);
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
        _shellWatchdog?.Dispose();
        _trayIconService?.Dispose();
        _instanceGuard?.Dispose();

        Shutdown();
    }
}
