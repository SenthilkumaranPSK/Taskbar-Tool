using Microsoft.Win32;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Persists and retrieves user preferences under HKCU\Software\Taskbar Tool.
/// </summary>
internal static class AppSettings
{
    private const string SettingsKeyPath = @"Software\Taskbar Tool";
    private const string HideWhenIdleValue = "HideWhenIdle";
    private const string TransparencyModeValue = "TransparencyMode";
    private const string GlobalHotkeyEnabledValue = "GlobalHotkeyEnabled";

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

    public static bool TransparencyMode
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(TransparencyModeValue) is int val)
                {
                    return val != 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read TransparencyMode setting: {ex.Message}");
            }

            return false; // Default: standard tinted acrylic pill
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(TransparencyModeValue, value ? 1 : 0, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save TransparencyMode setting: {ex.Message}");
            }
        }
    }

    private const string ShowProgressBarValue = "ShowProgressBar";
    private const string ShowEqualizerValue = "ShowEqualizer";

    public static bool GlobalHotkeyEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(GlobalHotkeyEnabledValue) is int val)
                {
                    return val != 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read GlobalHotkeyEnabled setting: {ex.Message}");
            }

            return true; // Default: Win+Shift+Z enabled
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(GlobalHotkeyEnabledValue, value ? 1 : 0, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save GlobalHotkeyEnabled setting: {ex.Message}");
            }
        }
    }

    public static bool ShowProgressBar
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(ShowProgressBarValue) is int val)
                {
                    return val != 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read ShowProgressBar setting: {ex.Message}");
            }

            return true; // Default: on
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(ShowProgressBarValue, value ? 1 : 0, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save ShowProgressBar setting: {ex.Message}");
            }
        }
    }

    public static bool ShowEqualizer
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(ShowEqualizerValue) is int val)
                {
                    return val != 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read ShowEqualizer setting: {ex.Message}");
            }

            return true; // Default: on
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(ShowEqualizerValue, value ? 1 : 0, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save ShowEqualizer setting: {ex.Message}");
            }
        }
    }
}
