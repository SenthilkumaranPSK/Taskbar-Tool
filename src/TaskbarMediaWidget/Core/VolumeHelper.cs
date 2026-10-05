using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Provides volume adjustments that invoke Windows' standard volume overlay (OSD).
/// </summary>
internal static class VolumeHelper
{
    public static void VolumeUp()
    {
        NativeMethods.keybd_event(NativeMethods.VK_VOLUME_UP, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_VOLUME_UP, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void VolumeDown()
    {
        NativeMethods.keybd_event(NativeMethods.VK_VOLUME_DOWN, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_VOLUME_DOWN, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void ToggleMute()
    {
        NativeMethods.keybd_event(NativeMethods.VK_VOLUME_MUTE, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        NativeMethods.keybd_event(NativeMethods.VK_VOLUME_MUTE, 0, NativeMethods.KEYEVENTF_EXTENDEDKEY | NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
