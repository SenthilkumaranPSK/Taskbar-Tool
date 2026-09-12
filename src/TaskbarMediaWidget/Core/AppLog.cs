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
    private const long MaxLogSizeBytes = 1 * 1024 * 1024;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarMediaWidget",
        "log.txt");

    private static readonly object WriteLock = new();

    private static string? _lastLine;
    private static int _repeatCount;

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
                var line = $"[{level}] {message}";

                // A state where something keeps failing on every poll tick (e.g. a taskbar
                // handle that never resolves) used to write a fresh WARN every ~1.3s forever —
                // tens of thousands of identical lines a day. Collapse runs of the exact same
                // line into a single entry with a trailing repeat count instead.
                if (line == _lastLine)
                {
                    _repeatCount++;
                    return;
                }

                var dir = Path.GetDirectoryName(LogPath);
                if (dir is not null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                FlushPendingRepeat();
                RotateIfOversized();

                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
                _lastLine = line;
                _repeatCount = 0;
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    private static void FlushPendingRepeat()
    {
        if (_repeatCount <= 0 || _lastLine is null)
        {
            return;
        }

        File.AppendAllText(
            LogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {_lastLine} (repeated {_repeatCount} more time{(_repeatCount == 1 ? string.Empty : "s")}){Environment.NewLine}");
    }

    private static void RotateIfOversized()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaxLogSizeBytes)
        {
            return;
        }

        var backupPath = LogPath + ".old";
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        File.Move(LogPath, backupPath);
    }
}
