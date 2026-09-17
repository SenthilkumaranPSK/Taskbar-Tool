using Microsoft.Win32;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// "Run at startup" support, backed by HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
///
/// HKCU rather than HKLM on purpose, for the same reason the single-instance mutex is Local\-scoped
/// (see <see cref="SingleInstanceGuard"/>): the widget is a per-user thing — every logged-on user
/// gets their own explorer.exe and their own taskbar — and HKCU needs no elevation, so the tray
/// toggle can just work instead of prompting for admin.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarMediaWidget";

    /// <summary>
    /// True only when the Run entry points at <em>this</em> executable. A stale entry left behind
    /// by a copy that has since been moved or rebuilt elsewhere counts as "not enabled" — showing
    /// a tick for a path that no longer launches this build would be a lie, and re-enabling is
    /// what rewrites the path to the current one.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(ValueName) is not string registered || string.IsNullOrWhiteSpace(registered))
            {
                return false;
            }

            return string.Equals(registered.Trim(), BuildLaunchCommand(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read startup registration: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes or removes the Run entry. Returns false (and logs) if the registry rejected it.</summary>
    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                AppLog.Warn("Could not open the Run key for writing; startup setting not changed.");
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, BuildLaunchCommand(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            AppLog.Info($"Run at startup {(enabled ? "enabled" : "disabled")}.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to update startup registration", ex);
            return false;
        }
    }

    /// <summary>
    /// The quoted command Windows should run at logon. Quoting is required, not cosmetic — the
    /// repo lives under paths with spaces ("My Codzz"), and an unquoted Run value is parsed at the
    /// first space.
    ///
    /// Normally this is just the apphost .exe. The dotnet-host case matters during development:
    /// launched as `dotnet TaskbarMediaWidget.dll`, ProcessPath is dotnet.exe, and registering
    /// that alone would produce an entry that starts the SDK host with no assembly and silently
    /// does nothing — so pass the managed assembly along with it.
    /// </summary>
    private static string BuildLaunchCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return $"\"{AppContext.BaseDirectory}TaskbarMediaWidget.exe\"";
        }

        // Fully qualified: Path is ambiguous with System.Windows.Shapes.Path in a WPF project.
        var fileName = System.IO.Path.GetFileNameWithoutExtension(processPath);
        if (!string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\"";
        }

        var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(assemblyPath)
            ? $"\"{processPath}\""
            : $"\"{processPath}\" \"{assemblyPath}\"";
    }
}
