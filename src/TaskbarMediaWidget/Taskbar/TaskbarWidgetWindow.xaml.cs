using System.Runtime.InteropServices;
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
/// this window's HWND as a side effect (it's a true child now) — this window has no way to know
/// that happened from the inside, since a destroyed HWND can't run any more code. It can only
/// notice on the next poll tick that its own handle is gone (see the <see cref="NativeMethods.IsWindow"/>
/// check in <see cref="TryReposition"/>) and stop trying to use it. Recovery itself is owned by
/// <c>App</c> via <see cref="ShellWatchdogWindow"/> — a separate, never-reparented window that
/// stays eligible to receive "TaskbarCreated" for the life of the process and drives recreation
/// of a fresh instance of this class.
/// </summary>
public partial class TaskbarWidgetWindow : Window
{
    private const int ActivePollIntervalMs = 1300;
    private const int HiddenPollIntervalMs = 5000;

    private readonly DispatcherTimer _pollTimer;

    private IntPtr _hwnd;
    private IntPtr _taskbarHandle;
    private HwndSource? _hwndSource;
    private bool _isSetUp;
    private int _lastMeasuredContentVersion = -1;
    private System.Windows.Size _lastNaturalSize;
    private (int X, int Y, int Width, int Height)? _lastAppliedRect;

    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;

    public TaskbarWidgetWindow()
    {
        InitializeComponent();

        WindowStyleHelper.SetNoActivate(this);
        Widget.ApplyTheme(ThemeDetector.IsSystemLightTheme());

        Widget.PreviousRequested += (_, _) => PreviousRequested?.Invoke(this, EventArgs.Empty);
        Widget.PlayPauseRequested += (_, _) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);
        Widget.NextRequested += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(HiddenPollIntervalMs),
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
            _pollTimer.Interval = TimeSpan.FromMilliseconds(HiddenPollIntervalMs);
            return;
        }

        TryReposition();
        SetWindowVisible(true);
        _pollTimer.Interval = TimeSpan.FromMilliseconds(ActivePollIntervalMs);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        AttachToTaskbar();

        // Start hidden regardless of whether AttachToTaskbar succeeded — a freshly (re)created
        // window has no now-playing state yet, and UpdateNowPlaying's own hide-if-null call would
        // otherwise leave a brief window (pun intended) where an empty box could be shown.
        SetWindowVisible(false);

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
        _lastAppliedRect = null; // Force a fresh SetWindowPos next tick — we just got a new parent.
        AppLog.Info("Attached widget window to taskbar.");
    }

    private void TryReposition()
    {
        if (_hwnd != IntPtr.Zero && !NativeMethods.IsWindow(_hwnd))
        {
            // The taskbar was destroyed out from under us (explorer.exe restart) — since we were
            // a true WS_CHILD, our own HWND was destroyed as a side effect and there is nothing
            // left here to reposition. Stop polling; ShellWatchdogWindow (in App) will drive
            // recreation of a fresh instance once Explorer comes back.
            _pollTimer.Stop();
            AppLog.Info("Widget HWND no longer exists (Explorer restart); stopping poll.");
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

        // A full WPF measure plus a Width/Height assignment (itself another layout pass) is
        // wasted work on the ~99% of ticks where the content hasn't changed since last time —
        // only redo it when the widget's own content-version counter says otherwise.
        if (Widget.ContentVersion != _lastMeasuredContentVersion)
        {
            _lastNaturalSize = Widget.MeasureNaturalSize();
            Width = _lastNaturalSize.Width;
            Height = _lastNaturalSize.Height;
            _lastMeasuredContentVersion = Widget.ContentVersion;
        }

        var startX = (int)Math.Round(LeftEdgeMarginLogicalPx * dpiScale);
        var physicalWidth = (int)Math.Round(_lastNaturalSize.Width * dpiScale);
        var physicalHeight = (int)Math.Round(_lastNaturalSize.Height * dpiScale);
        var y = (int)Math.Round((taskbarRect.Height - physicalHeight) / 2.0);

        var appliedRect = (startX, y, physicalWidth, physicalHeight);
        if (appliedRect == _lastAppliedRect)
        {
            return; // Identical to what's already on screen — skip the SetWindowPos call.
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            startX,
            y,
            physicalWidth,
            physicalHeight,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS | NativeMethods.SWP_SHOWWINDOW);

        _lastAppliedRect = appliedRect;
    }

    private void SetWindowVisible(bool visible)
    {
        if (!visible)
        {
            Widget.StopMarquee();
        }

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
        if (msg is NativeMethods.WM_DPICHANGED or NativeMethods.WM_DPICHANGED_AFTERPARENT)
        {
            TryReposition();
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            TryReposition();
        }
        else if (msg == NativeMethods.WM_SETTINGCHANGE)
        {
            if ((int)(long)wParam == NativeMethods.SPI_SETWORKAREA)
            {
                TryReposition();
            }

            // Broadcast with lParam pointing at the literal string "ImmersiveColorSet" whenever
            // the user flips Settings > Personalization > Colors between light and dark.
            if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                Widget.ApplyTheme(ThemeDetector.IsSystemLightTheme());
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        _hwndSource?.RemoveHook(WndProc);
        base.OnClosed(e);
    }
}
