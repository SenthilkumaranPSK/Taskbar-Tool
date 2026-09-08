using Microsoft.Win32;
using System.Windows.Automation;
using TaskbarMediaWidget.Core;
using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Taskbar;

public enum TaskbarAlignment
{
    Left,
    Center,
}

/// <summary>
/// Locates the right edge of the Start button, in taskbar client coordinates, so the widget can
/// be placed immediately to its right. This is the one piece of the whole app with no working
/// prior art to lean on (FluentFlyout never looks up the Start button — see
/// docs/reference-fluentflyout-taskbar-widget.md) and it needs on-machine verification:
///
///   - The automation ID guessed below ("StartButton") resolved successfully on a Windows 11
///     (build 10.0.26200) dev machine, but that's one build/config, not a guarantee — if it
///     doesn't resolve on yours, every lookup silently falls through to the fallback path; check
///     the log (%LocalAppData%\TaskbarMediaWidget\log.txt) for "StartButton automation id did
///     not resolve" to find out whether that's happening on your machine.
///   - FallbackStartButtonWidthLogicalPx below is a placeholder and MUST be measured on the
///     real target machine (e.g. a screenshot + pixel ruler) before relying on the fallback path.
/// </summary>
internal static class StartButtonLocator
{
    // TODO(verify on-device): unverified guess, first thing to confirm via AppLog output.
    private const string StartButtonAutomationId = "StartButton";

    // TODO(measure on-device): placeholder only — replace with a measured value before shipping.
    private const double FallbackStartButtonWidthLogicalPx = 54;

    private const double GapAfterStartButtonLogicalPx = 6;

    private const double MinPlausibleWidthLogicalPx = 32;
    private const double MaxPlausibleWidthLogicalPx = 80;

    // Caches the resolved Start button element the same way TaskbarLocator caches the taskbar
    // frame: a UI Automation FindFirst tree walk is the expensive part of this lookup, and the
    // caller re-invokes GetWidgetStartX on every ~1.3s poll tick (and on every DPI/display-change
    // message) on the UI dispatcher thread. Re-walking the tree every tick would mean a bounded
    // but still blocking Task.Run+Wait on the UI thread each time; re-reading BoundingRectangle
    // on an already-resolved element is a single cheap property fetch instead.
    private static AutomationElement? _cachedStartButton;
    private static IntPtr _cachedForHwnd;

    /// <summary>
    /// Returns the X coordinate (taskbar client space, physical px) where the widget should
    /// start, immediately right of the Start button, or null if even the fallback can't produce
    /// a sane answer (e.g. Center-aligned taskbar with automation unavailable — see remarks).
    /// </summary>
    public static int? GetWidgetStartX(IntPtr taskbarHandle, NativeMethods.RECT taskbarRectScreen, double dpiScale)
    {
        var automationResult = TryGetFromAutomation(taskbarHandle, taskbarRectScreen, dpiScale);
        if (automationResult is { } x)
        {
            return x;
        }

        return TryGetFromFallback(taskbarRectScreen, dpiScale);
    }

    private static int? TryGetFromAutomation(IntPtr taskbarHandle, NativeMethods.RECT taskbarRectScreen, double dpiScale)
    {
        var element = TryGetCachedOrFreshStartButton(taskbarHandle);
        if (element is null)
        {
            return null;
        }

        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty)
            {
                return null;
            }

            var widthLogical = bounds.Width / dpiScale;
            var heightLogical = bounds.Height / dpiScale;
            var taskbarHeightLogical = taskbarRectScreen.Height / dpiScale;

            var withinTaskbarBounds =
                bounds.Left >= taskbarRectScreen.Left - 2 &&
                bounds.Right <= taskbarRectScreen.Right + 2 &&
                bounds.Top >= taskbarRectScreen.Top - 2 &&
                bounds.Bottom <= taskbarRectScreen.Bottom + 2;

            var plausibleHeight = heightLogical >= taskbarHeightLogical * 0.5;
            var plausibleWidth = widthLogical is >= MinPlausibleWidthLogicalPx and <= MaxPlausibleWidthLogicalPx;

            if (!withinTaskbarBounds || !plausibleHeight || !plausibleWidth)
            {
                AppLog.Warn(
                    $"StartButton automation result failed sanity check " +
                    $"(withinBounds={withinTaskbarBounds}, height={heightLogical:F0}, width={widthLogical:F0}); using fallback offset.");
                return null;
            }

            var gapPhysical = (int)Math.Round(GapAfterStartButtonLogicalPx * dpiScale);
            var screenPoint = new NativeMethods.POINT { X = (int)bounds.Right + gapPhysical, Y = (int)bounds.Top };
            NativeMethods.ScreenToClient(taskbarHandle, ref screenPoint);
            return screenPoint.X;
        }
        catch (ElementNotAvailableException)
        {
            _cachedStartButton = null;
            return null;
        }
    }

    private static AutomationElement? TryGetCachedOrFreshStartButton(IntPtr taskbarHandle)
    {
        if (_cachedStartButton != null && _cachedForHwnd == taskbarHandle)
        {
            try
            {
                _ = _cachedStartButton.Current.BoundingRectangle; // touch it to force staleness check
                return _cachedStartButton;
            }
            catch (ElementNotAvailableException)
            {
                _cachedStartButton = null;
            }
        }

        var found = AutomationLookup.TryFindByAutomationId(taskbarHandle, StartButtonAutomationId, timeoutMs: 1000);
        if (found != null)
        {
            _cachedStartButton = found;
            _cachedForHwnd = taskbarHandle;
        }
        else
        {
            AppLog.Warn($"StartButton automation id '{StartButtonAutomationId}' did not resolve; using fallback offset.");
        }

        return found;
    }

    private static int? TryGetFromFallback(NativeMethods.RECT taskbarRectScreen, double dpiScale)
    {
        // The fixed offset only makes sense measured from the taskbar's own left edge, which is
        // only where Start actually sits when the taskbar is Left-aligned (Windows 10 default,
        // and a common Windows 11 preference). In Center alignment (the Windows 11 default),
        // Start floats near the middle of the screen at a position that depends on how many
        // other icons are pinned — a fixed left-edge offset would be visibly wrong there, so we
        // deliberately refuse to guess rather than silently misplacing the widget.
        if (GetTaskbarAlignment() == TaskbarAlignment.Center)
        {
            AppLog.Warn("Taskbar is Center-aligned and automation lookup failed; refusing to guess a fixed offset.");
            return null;
        }

        var offsetPhysical = (int)Math.Round((FallbackStartButtonWidthLogicalPx + GapAfterStartButtonLogicalPx) * dpiScale);
        return offsetPhysical;
    }

    private static TaskbarAlignment GetTaskbarAlignment()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var value = key?.GetValue("TaskbarAl");
            // 0 = Left, 1 = Center (Windows 11 default). Any unexpected/missing value: assume
            // the modern default (Center) so we don't silently apply a Left-only fallback.
            return value is int intValue && intValue == 0 ? TaskbarAlignment.Left : TaskbarAlignment.Center;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not read taskbar alignment from registry: {ex.Message}");
            return TaskbarAlignment.Center;
        }
    }
}
