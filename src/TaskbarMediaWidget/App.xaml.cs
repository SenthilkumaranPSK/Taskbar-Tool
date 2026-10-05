using System.Windows;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Flyouts;
using TaskbarMediaWidget.Interop;
using TaskbarMediaWidget.Media;
using TaskbarMediaWidget.Taskbar;
using TaskbarMediaWidget.Tray;

namespace TaskbarMediaWidget;

public partial class App : System.Windows.Application
{
    private const int ExplorerReadyPollIntervalMs = 200;
    private const int ExplorerReadyTimeoutMs = 60_000;

    // WPF's startup cost (XAML parse, first layout/render, JIT) leaves a working set much larger
    // than the steady state needs, and an idle tray process never comes under the memory pressure
    // that would reclaim it. Trim once the startup burst has settled.
    private const int StartupSettleMs = 15_000;

    private SingleInstanceGuard? _instanceGuard;
    private MediaSessionService? _mediaSessionService;
    private TrayIconService? _trayIconService;
    private TaskbarWidgetWindow? _widgetWindow;
    private MediaFlyoutWindow? _flyoutWindow;
    private ShellWatchdogWindow? _shellWatchdog;
    private HardwareMonitorService? _hardwareMonitorService;
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

        AppLog.Info("Starting Taskbar Tool.");

        _hardwareMonitorService = new HardwareMonitorService();
        _hardwareMonitorService.UsageUpdated += OnHardwareUsageUpdated;

        _mediaSessionService = new MediaSessionService();
        _trayIconService = new TrayIconService();
        _trayIconService.ExitRequested += (_, _) => ExitApplication();
        _trayIconService.FlyoutRequested += (_, _) => ShowFlyoutFromTray();

        _flyoutWindow = new MediaFlyoutWindow();
        _flyoutWindow.PreviousRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.PreviousAsync());
        _flyoutWindow.PlayPauseRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.PlayPauseAsync());
        _flyoutWindow.NextRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.NextAsync());
        _flyoutWindow.ShuffleRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.ToggleShuffleAsync());
        _flyoutWindow.RepeatRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.ToggleRepeatAsync());
        _flyoutWindow.SeekRequested += target => _ = RunCommandAsync(_mediaSessionService!.SeekAsync(target));

        AppSettings.SettingsChanged += (_, _) => Dispatcher.Invoke(() => _widgetWindow?.UpdateNowPlaying(_mediaSessionService?.CurrentNowPlaying));

        // Never reparented, so — unlike TaskbarWidgetWindow — it stays eligible to receive
        // "TaskbarCreated" for the life of the process. See ShellWatchdogWindow for why.
        _shellWatchdog = new ShellWatchdogWindow();
        _shellWatchdog.TaskbarCreated += (_, _) => _ = HandleExplorerRestartAsync();

        // Start the media pipeline before the window exists, giving its async session-property
        // reads a head start — CreateWidgetWindow also starts the widget hidden regardless (see
        // TaskbarWidgetWindow.OnSourceInitialized), so there's no visible empty-box moment either way.
        _mediaSessionService.Start();
        _hardwareMonitorService.Start();
        CreateWidgetWindow();

        ScheduleStartupTrim();
    }

    private void ScheduleStartupTrim()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(StartupSettleMs),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            MemoryTrimmer.TrimIfIdle("startup settled");
        };

        timer.Start();
    }

    private void CreateWidgetWindow()
    {
        _widgetWindow = new TaskbarWidgetWindow();
        _widgetWindow.PreviousRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.PreviousAsync());
        _widgetWindow.PlayPauseRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.PlayPauseAsync());
        _widgetWindow.NextRequested += (_, _) => _ = RunCommandAsync(_mediaSessionService!.NextAsync());
        _widgetWindow.FlyoutToggleRequested += (_, _) => _flyoutWindow?.ToggleFlyout(fromTray: false);

        if (_mediaSessionService is not null)
        {
            _mediaSessionService.NowPlayingChanged += OnNowPlayingChanged;
        }

        _widgetWindow.Show();

        // Pick up whatever was already playing rather than waiting for the next SMTC event —
        // matters most right after an Explorer restart, where this is a brand-new window.
        var initialNowPlaying = _mediaSessionService?.CurrentNowPlaying;
        _widgetWindow.UpdateNowPlaying(initialNowPlaying);
        _flyoutWindow?.UpdateNowPlaying(initialNowPlaying);

        if (_hardwareMonitorService is not null)
        {
            _widgetWindow.UpdateHardwareStats(_hardwareMonitorService.CurrentCpuPercent, _hardwareMonitorService.CurrentRamPercent);
            _flyoutWindow?.UpdateHardwareStats(_hardwareMonitorService.CurrentCpuPercent, _hardwareMonitorService.CurrentRamPercent);
        }
    }

    private void ShowFlyoutFromTray()
    {
        _flyoutWindow?.ToggleFlyout(fromTray: true);
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
            CloseWidgetWindow(_widgetWindow);
        }

        CreateWidgetWindow();
    }

    // WPF isn't always graceful about Close() on a window whose HWND was already destroyed
    // externally (as ours is after an Explorer restart) — guard it rather than let a teardown
    // exception here take down the rest of shutdown/recreation.
    private static void CloseWidgetWindow(TaskbarWidgetWindow window)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to close widget window cleanly: {ex.Message}");
        }
    }

    private void OnNowPlayingChanged(NowPlayingInfo? info) =>
        Dispatcher.InvokeAsync(() =>
        {
            _widgetWindow?.UpdateNowPlaying(info);
            _flyoutWindow?.UpdateNowPlaying(info);
        });

    private void OnHardwareUsageUpdated(int cpu, int ram) =>
        Dispatcher.InvokeAsync(() =>
        {
            _widgetWindow?.UpdateHardwareStats(cpu, ram);
            _flyoutWindow?.UpdateHardwareStats(cpu, ram);
        });

    private void ExitApplication()
    {
        AppLog.Info("Exit requested from tray icon.");

        if (_hardwareMonitorService is not null)
        {
            _hardwareMonitorService.UsageUpdated -= OnHardwareUsageUpdated;
            _hardwareMonitorService.Dispose();
        }

        if (_mediaSessionService is not null)
        {
            _mediaSessionService.NowPlayingChanged -= OnNowPlayingChanged;
            _mediaSessionService.Dispose();
        }

        if (_widgetWindow is not null)
        {
            CloseWidgetWindow(_widgetWindow);
        }

        _flyoutWindow?.Close();
        _shellWatchdog?.Dispose();
        _trayIconService?.Dispose();
        _instanceGuard?.Dispose();

        Shutdown();
    }
}
