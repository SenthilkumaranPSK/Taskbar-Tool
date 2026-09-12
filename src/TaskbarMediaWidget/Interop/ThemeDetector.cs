using Microsoft.Win32;

namespace TaskbarMediaWidget.Interop;

/// <summary>
/// Reads whether the taskbar/Start-menu chrome is currently using Windows' light theme, so the
/// widget can swap its (otherwise hardcoded white) text and hover brushes accordingly — on a
/// Light-mode taskbar those brushes would otherwise be near-invisible against a near-white
/// background.
/// </summary>
internal static class ThemeDetector
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "SystemUsesLightTheme";

    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue(ValueName) is int value)
            {
                return value != 0;
            }
        }
        catch
        {
            // Fall through to the dark-theme default below, which is what the widget's brushes
            // were originally hardcoded against.
        }

        return false;
    }
}
