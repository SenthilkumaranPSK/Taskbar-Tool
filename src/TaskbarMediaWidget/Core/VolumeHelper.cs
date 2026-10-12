namespace TaskbarMediaWidget.Core;

/// <summary>
/// Provides high-precision volume adjustments via native CoreAudio APIs.
/// </summary>
internal static class VolumeHelper
{
    private static readonly CoreAudioVolumeService Service = new();

    public static CoreAudioVolumeService Current => Service;

    public static int GetVolumePercent() => Service.GetVolumePercent();

    public static void SetVolumePercent(int percent) => Service.SetVolumePercent(percent);

    public static bool IsMuted() => Service.IsMuted();

    public static void VolumeUp() => Service.VolumeUp(stepPercent: 2);

    public static void VolumeDown() => Service.VolumeDown(stepPercent: 2);

    public static void ToggleMute() => Service.ToggleMute();
}
