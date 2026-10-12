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

    private const string WidgetLayoutValue = "WidgetLayout";
    private const string TaskbarOffsetPxValue = "TaskbarOffsetPx";
    private const string DiscordRpcEnabledValue = "DiscordRpcEnabled";
    private const string RealSpectrumEnabledValue = "RealSpectrumEnabled";
    private const string IgnoredAppsValue = "IgnoredApps";
    private const string ShowNetworkStatsValue = "ShowNetworkStats";

    public static WidgetLayoutStyle WidgetLayout
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(WidgetLayoutValue) is int val && Enum.IsDefined(typeof(WidgetLayoutStyle), val))
                {
                    return (WidgetLayoutStyle)val;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read WidgetLayout setting: {ex.Message}");
            }

            return WidgetLayoutStyle.Standard;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(WidgetLayoutValue, (int)value, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save WidgetLayout setting: {ex.Message}");
            }
        }
    }

    public static int TaskbarOffsetPx
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(TaskbarOffsetPxValue) is int val)
                {
                    return val;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read TaskbarOffsetPx setting: {ex.Message}");
            }

            return 0;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(TaskbarOffsetPxValue, value, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save TaskbarOffsetPx setting: {ex.Message}");
            }
        }
    }

    public static bool DiscordRpcEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(DiscordRpcEnabledValue) is int val)
                {
                    return val != 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read DiscordRpcEnabled setting: {ex.Message}");
            }

            return true;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(DiscordRpcEnabledValue, value ? 1 : 0, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save DiscordRpcEnabled setting: {ex.Message}");
            }
        }
    }

    public static bool RealSpectrumEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(RealSpectrumEnabledValue) is int val)
                {
                    return val != 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read RealSpectrumEnabled setting: {ex.Message}");
            }

            return true;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(RealSpectrumEnabledValue, value ? 1 : 0, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save RealSpectrumEnabled setting: {ex.Message}");
            }
        }
    }

    public static string IgnoredApps
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(IgnoredAppsValue) is string val)
                {
                    return val;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read IgnoredApps setting: {ex.Message}");
            }

            return string.Empty;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(IgnoredAppsValue, value ?? string.Empty, RegistryValueKind.String);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save IgnoredApps setting: {ex.Message}");
            }
        }
    }

    public static bool ShowNetworkStats
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                if (key?.GetValue(ShowNetworkStatsValue) is int val)
                {
                    return val != 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to read ShowNetworkStats setting: {ex.Message}");
            }

            return true;
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                key?.SetValue(ShowNetworkStatsValue, value ? 1 : 0, RegistryValueKind.DWord);
                SettingsChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to save ShowNetworkStats setting: {ex.Message}");
            }
        }
    }
}

public enum WidgetLayoutStyle
{
    Standard = 0,
    Compact = 1,
    Minimal = 2,
    Wide = 3
}
