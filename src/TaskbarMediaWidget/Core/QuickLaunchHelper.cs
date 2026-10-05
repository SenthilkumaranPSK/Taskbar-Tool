using System.Diagnostics;
using System.IO;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Safe application and utility launcher for Taskbar Tool's quick launch buttons.
/// </summary>
internal static class QuickLaunchHelper
{
    public static void LaunchChrome()
    {
        // Try direct command first
        if (TryStart("chrome"))
        {
            return;
        }

        // Try standard installation paths
        var candidates = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path) && TryStart(path))
            {
                return;
            }
        }

        // Fallback to default browser
        TryStartUrl("https://www.google.com");
    }

    public static void LaunchExplorer() => TryStart("explorer.exe");

    public static void LaunchTerminal()
    {
        // Try Windows Terminal (wt.exe) first, fallback to PowerShell
        if (!TryStart("wt.exe"))
        {
            TryStart("powershell.exe");
        }
    }

    public static void LaunchNotepad() => TryStart("notepad.exe");

    public static void LaunchCalculator() => TryStart("calc.exe");

    public static void LaunchSnippingTool()
    {
        // ms-screenclip: is the Windows 10/11 native protocol for the Snipping Tool overlay
        TryStartUrl("ms-screenclip:");
    }

    public static void LaunchTaskManager() => TryStart("taskmgr.exe");

    public static void LockWorkstation() => TryStart("rundll32.exe", "user32.dll,LockWorkStation");

    public static bool TryStart(string fileName, string arguments = "")
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = true,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to launch '{fileName}': {ex.Message}");
            return false;
        }
    }

    public static bool TryStartUrl(string url)
    {
        try
        {
            var psi = new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to open URL/protocol '{url}': {ex.Message}");
            return false;
        }
    }
}
