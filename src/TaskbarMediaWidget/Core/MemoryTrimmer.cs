using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Releases the process's idle working set back to the OS.
///
/// <para>
/// This app spends almost all of its life doing nothing: a timer tick every few seconds, and an
/// SMTC callback when a track changes. WPF's startup allocations (XAML parsing, the first layout
/// and render pass, JIT) leave a working set far larger than the steady state actually needs, and
/// nothing reclaims it on its own because the process never comes under memory pressure.
/// </para>
/// <para>
/// Deliberately throttled and event-driven rather than periodic. Emptying the working set forces
/// pages to be faulted back in on next use, so doing it aggressively trades a smaller number in
/// Task Manager for exactly the stutter this is supposed to avoid. It runs after startup settles
/// and when the widget goes idle (nothing playing), never on the poll tick.
/// </para>
/// </summary>
internal static class MemoryTrimmer
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(2);
    private static readonly object Gate = new();

    private static DateTime _lastTrimUtc = DateTime.MinValue;

    /// <summary>
    /// Trims if enough time has passed since the last trim. Returns silently otherwise — callers
    /// are free to invoke this on any "we just went idle" signal without tracking timing.
    /// </summary>
    public static void TrimIfIdle(string reason)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastTrimUtc < MinimumInterval)
            {
                return;
            }

            _lastTrimUtc = now;
        }

        try
        {
            // -1/-1 is the documented "use the default minimum/maximum", which tells the memory
            // manager to trim the working set to what the process is actually touching.
            if (!NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1)))
            {
                AppLog.Warn("Working-set trim was rejected by the OS.");
                return;
            }

            AppLog.Info($"Trimmed working set ({reason}).");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Working-set trim failed: {ex.Message}");
        }
    }
}
