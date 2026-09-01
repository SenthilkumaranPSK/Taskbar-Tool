using System.IO;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Minimal append-to-file logger. Taskbar-embedding bugs (Explorer restarts, DPI changes,
/// automation lookups going stale) are exactly the kind of thing that's hard to reproduce under
/// a debugger, so a persistent log is worth the handful of lines this costs. Logging failures
/// are swallowed — logging must never be the thing that crashes the app.
/// </summary>
internal static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMediaWidget",
        "log.txt");

    private static readonly object WriteLock = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} — {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (WriteLock)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
