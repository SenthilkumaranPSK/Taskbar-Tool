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
    private const string ValueName = "Taskbar Tool";

    // The pre-1.1 value name. Removed whenever the toggle is touched, so upgrading from an older
    // build can't leave two Run entries both launching the widget at logon.
    private const string LegacyValueName = "TaskbarMediaWidget";

    // Must track <AssemblyName> in the .csproj.
    private const string ExecutableName = "TaskbarTool.exe";
    private const string AssemblyFileName = "TaskbarTool.dll";

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

            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

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
    /// Normally this is just the apphost .exe (published or built), which is what ProcessPath
    /// reports. The dotnet-host case matters during development: launched as
    /// `dotnet TaskbarTool.dll`, ProcessPath is dotnet.exe, and registering that alone would
    /// produce an entry that starts the SDK host with no assembly and silently does nothing — so
    /// pass the managed assembly along with it.
    /// </summary>
    private static string BuildLaunchCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return $"\"{System.IO.Path.Combine(AppContext.BaseDirectory, ExecutableName)}\"";
        }

        // Fully qualified: Path is ambiguous with System.Windows.Shapes.Path in a WPF project.
        var fileName = System.IO.Path.GetFileNameWithoutExtension(processPath);
        if (!string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\"";
        }

        // Deliberately not Assembly.Location: it returns an empty string in a single-file app
        // (IL3000), which is exactly how this ships. BaseDirectory is correct in both layouts.
        var assemblyPath = System.IO.Path.Combine(AppContext.BaseDirectory, AssemblyFileName);
        return System.IO.File.Exists(assemblyPath)
            ? $"\"{processPath}\" \"{assemblyPath}\""
            : $"\"{processPath}\"";
    }
}
