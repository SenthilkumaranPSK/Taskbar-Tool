using Microsoft.Win32;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Persists and retrieves user preferences under HKCU\Software\Taskbar Tool.
/// </summary>
internal static class AppSettings
{
    private const string SettingsKeyPath = @"Software\Taskbar Tool";
    private const string HideWhenIdleValue = "HideWhenIdle";

    public static event EventHandler? SettingsChanged;

    public static bool HideWhenIdle
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(HideWhenIdleValue) is int val)
                {
                    return val != 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read HideWhenIdle setting: {ex.Message}");
            }

            return false; // Default: show friendly standby pill rather than hiding into nothingness
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(HideWhenIdleValue, value ? 1 : 0, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save HideWhenIdle setting: {ex.Message}");
            }
        }
    }
}
