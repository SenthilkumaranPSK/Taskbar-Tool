using System.Windows.Automation;
using TaskbarMediaWidget.Core;

namespace TaskbarMediaWidget.Taskbar;

/// <summary>
/// Shared, bounded-time UI Automation element lookup within the taskbar's own tree. Automation
/// calls can block for a long time (or hang) against a busy/unresponsive shell, so every lookup
/// runs on a background task with a hard timeout rather than being called inline.
/// </summary>
internal static class AutomationLookup
{
    public static AutomationElement? TryFindByAutomationId(IntPtr taskbarHandle, string automationId, int timeoutMs)
    {
        try
        {
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(taskbarHandle);
                var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
                return root.FindFirst(TreeScope.Descendants, condition);
            });

            if (task.Wait(timeoutMs))
            {
                return task.Result;
            }

            AppLog.Warn($"Automation lookup for '{automationId}' timed out after {timeoutMs}ms.");
            return null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or AggregateException)
        {
            AppLog.Warn($"Automation lookup for '{automationId}' failed: {ex.Message}");
            return null;
        }
    }
}
