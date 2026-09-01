using System.Windows;
using System.Windows.Interop;

namespace TaskbarMediaWidget.Interop;

/// <summary>
/// Small helpers for preparing a WPF window to live as a reparented child of another
/// process's window (the taskbar), where WPF's own activation/topmost handling doesn't apply.
/// </summary>
internal static class WindowStyleHelper
{
    /// <summary>
    /// Prevents the window from ever taking keyboard focus or activating — reparenting it into
    /// the taskbar must never steal focus away from whatever the user is doing.
    /// </summary>
    public static void SetNoActivate(Window window)
    {
        window.ShowActivated = false;

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            var hwnd = new WindowInteropHelper(window).Handle;
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_NOACTIVATE));
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Flips the window's style from WS_POPUP (a normal top-level WPF window) to WS_CHILD, which
    /// is a prerequisite for SetParent to make it a genuine child of the taskbar rather than a
    /// separate top-level window that merely tracks the taskbar's position.
    /// </summary>
    public static void MakeChildWindow(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~NativeMethods.WS_POPUP;
        style |= NativeMethods.WS_CHILD;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));
    }
}
