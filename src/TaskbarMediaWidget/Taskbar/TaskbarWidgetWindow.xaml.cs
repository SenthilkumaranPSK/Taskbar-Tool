using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Interop;
using TaskbarMediaWidget.Media;

namespace TaskbarMediaWidget.Taskbar;

/// <summary>
/// The window that gets reparented as a real WS_CHILD of the taskbar (Shell_TrayWnd). See
/// docs/reference-fluentflyout-taskbar-widget.md for why each piece of this exists.
///
/// Recreate, don't revive: when explorer.exe restarts, DestroyWindow on the taskbar destroys
/// this window's HWND as a side effect (it's a true child now). App.xaml.cs is responsible for
/// disposing this instance and constructing a fresh one when <see cref="ExplorerRestarted"/>
/// fires — this class does not attempt to resurrect itself.
/// </summary>
public partial class TaskbarWidgetWindow : Window
{
    private const int PollIntervalMs = 1300;
    private const int ExplorerReadyPollIntervalMs = 200;
    private const int ExplorerReadyTimeoutMs = 60_000;

    private readonly DispatcherTimer _pollTimer;
    private readonly uint _taskbarCreatedMessage;

    private IntPtr _hwnd;
    private IntPtr _taskbarHandle;
    private HwndSource? _hwndSource;
    private bool _explorerRestarting;
    private bool _isSetUp;

    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;

    /// <summary>Fires once Explorer is confirmed back up after a restart; caller should recreate this window.</summary>
    public event EventHandler? ExplorerRestarted;

    public TaskbarWidgetWindow()
    {
        InitializeComponent();

        WindowStyleHelper.SetNoActivate(this);

        Widget.PreviousRequested += (_, _) => PreviousRequested?.Invoke(this, EventArgs.Empty);
        Widget.PlayPauseRequested += (_, _) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);
        Widget.NextRequested += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
        };
        _pollTimer.Tick += (_, _) => TryReposition();

        SourceInitialized += OnSourceInitialized;
    }

    public void UpdateNowPlaying(NowPlayingInfo? info)
    {
        Widget.SetNowPlaying(info);

        if (!_isSetUp)
        {
            return;
        }

        if (info is null)
        {
            SetWindowVisible(false);
            return;
        }

        TryReposition();
        SetWindowVisible(true);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        AttachToTaskbar();
        _pollTimer.Start();
    }

    private void AttachToTaskbar()
    {
        _taskbarHandle = TaskbarLocator.FindTaskbarHandle();
        if (_taskbarHandle == IntPtr.Zero)
        {
            AppLog.Warn("Shell_TrayWnd not found yet; will retry on next poll.");
            return;
        }

        WindowStyleHelper.MakeChildWindow(_hwnd);
        NativeMethods.SetParent(_hwnd, _taskbarHandle);

        // SetParent's return value can't reliably indicate failure (NULL is also the correct
        // return for a window with no previous parent), so verify by reading the parent back.
        if (NativeMethods.GetParent(_hwnd) != _taskbarHandle)
        {
            AppLog.Warn("SetParent did not attach the widget to the taskbar; will retry on next poll.");
            return;
        }

        _isSetUp = true;
        AppLog.Info("Attached widget window to taskbar.");
    }

    private void TryReposition()
    {
        if (_explorerRestarting)
        {
            return;
        }

        var currentTaskbarHandle = TaskbarLocator.FindTaskbarHandle();
        if (currentTaskbarHandle == IntPtr.Zero)
        {
            return; // Explorer not up yet — leave things as they are, next tick will retry.
        }

        if (!_isSetUp || currentTaskbarHandle != _taskbarHandle || NativeMethods.GetParent(_hwnd) != currentTaskbarHandle)
        {
            _taskbarHandle = currentTaskbarHandle;
            AttachToTaskbar();
        }

        CalculateAndSetPosition();
    }

    // Anchored to the taskbar's own left edge, independent of the Start button — chosen over
    // docking next to Start because Start sits in the middle of the screen on a Center-aligned
    // taskbar (the Windows 11 default), and this widget is meant to always be reachable at the
    // same spot regardless of taskbar alignment or how many icons are pinned.
    private const double LeftEdgeMarginLogicalPx = 6;

    private void CalculateAndSetPosition()
    {
        if (_taskbarHandle == IntPtr.Zero || Widget.Visibility != Visibility.Visible)
        {
            return;
        }

        var dpiScale = TaskbarLocator.GetDpiScale(_taskbarHandle);
        var taskbarRect = TaskbarLocator.GetTaskbarRect(_taskbarHandle);

        var naturalSize = Widget.MeasureNaturalSize();
        Width = naturalSize.Width;
        Height = naturalSize.Height;

        var startX = (int)Math.Round(LeftEdgeMarginLogicalPx * dpiScale);
        var physicalWidth = (int)Math.Round(naturalSize.Width * dpiScale);
        var physicalHeight = (int)Math.Round(naturalSize.Height * dpiScale);
        var y = (int)Math.Round((taskbarRect.Height - physicalHeight) / 2.0);

        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            startX,
            y,
            physicalWidth,
            physicalHeight,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS | NativeMethods.SWP_SHOWWINDOW);
    }

    private void SetWindowVisible(bool visible)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            (visible ? NativeMethods.SWP_SHOWWINDOW : NativeMethods.SWP_HIDEWINDOW));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_taskbarCreatedMessage)
        {
            _ = HandleExplorerRestartAsync();
            return IntPtr.Zero;
        }

        if (msg is NativeMethods.WM_DPICHANGED or NativeMethods.WM_DPICHANGED_AFTERPARENT)
        {
            TryReposition();
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            TryReposition();
        }
        else if (msg == NativeMethods.WM_SETTINGCHANGE && wParam.ToInt32() == NativeMethods.SPI_SETWORKAREA)
        {
            TryReposition();
        }

        return IntPtr.Zero;
    }

    private async Task HandleExplorerRestartAsync()
    {
        if (_explorerRestarting)
        {
            return;
        }

        _explorerRestarting = true;
        _isSetUp = false;
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

        AppLog.Info("Taskbar is back; requesting widget window recreation.");
        _explorerRestarting = false;
        Dispatcher.Invoke(() => ExplorerRestarted?.Invoke(this, EventArgs.Empty));
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        _hwndSource?.RemoveHook(WndProc);
        base.OnClosed(e);
    }
}
