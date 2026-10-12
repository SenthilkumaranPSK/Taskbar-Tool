using System.Windows.Interop;
using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Registers and monitors global keyboard shortcuts (e.g. Win + Shift + Z) across the entire system.
/// Uses a lightweight, unparented HwndSource window message pump.
/// </summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x544B; // 'TK'
    private HwndSource? _hwndSource;
    private bool _isRegistered;

    public event EventHandler? HotkeyPressed;

    public GlobalHotkeyService()
    {
        var parameters = new HwndSourceParameters("TaskbarMediaWidget.HotkeyListener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)NativeMethods.WS_POPUP),
            ExtendedWindowStyle = (int)NativeMethods.WS_EX_NOACTIVATE,
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        if (AppSettings.GlobalHotkeyEnabled)
        {
            Register();
        }
    }

    public bool Register()
    {
        if (_hwndSource is null || _isRegistered)
        {
            return _isRegistered;
        }

        var modifiers = NativeMethods.MOD_WIN | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
        _isRegistered = NativeMethods.RegisterHotKey(_hwndSource.Handle, HotkeyId, modifiers, NativeMethods.VK_Z);

        if (_isRegistered)
        {
            AppLog.Info("Registered global shortcut Win+Shift+Z successfully.");
        }
        else
        {
            AppLog.Warn("Failed to register global shortcut Win+Shift+Z (may already be in use by another app).");
        }

        return _isRegistered;
    }

    public void Unregister()
    {
        if (_hwndSource is not null && _isRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HotkeyId);
            _isRegistered = false;
            AppLog.Info("Unregistered global shortcut Win+Shift+Z.");
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Register();
        }
        else
        {
            Unregister();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && (int)wParam == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
