using System.Windows.Threading;
using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Ultra-lightweight CPU and RAM hardware monitor.
/// Uses native Win32 GetSystemTimes and GlobalMemoryStatusEx for zero-overhead, sub-millisecond readings.
/// </summary>
internal sealed class HardwareMonitorService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private ulong _prevIdleTime;
    private ulong _prevKernelTime;
    private ulong _prevUserTime;
    private bool _hasPrevTimes;

    public event Action<int, int>? UsageUpdated;

    public int CurrentCpuPercent { get; private set; }
    public int CurrentRamPercent { get; private set; }

    public HardwareMonitorService(int intervalSeconds = 2)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(intervalSeconds),
        };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Refresh()
    {
        UpdateRamUsage();
        UpdateCpuUsage();
        UsageUpdated?.Invoke(CurrentCpuPercent, CurrentRamPercent);
    }

    private void UpdateRamUsage()
    {
        try
        {
            var memStatus = new NativeMethods.MEMORYSTATUSEX();
            if (NativeMethods.GlobalMemoryStatusEx(memStatus))
            {
                CurrentRamPercent = (int)memStatus.dwMemoryLoad;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read RAM usage: {ex.Message}");
        }
    }

    private void UpdateCpuUsage()
    {
        try
        {
            if (!NativeMethods.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            {
                return;
            }

            var idle = ToUInt64(idleFt);
            var kernel = ToUInt64(kernelFt);
            var user = ToUInt64(userFt);

            if (_hasPrevTimes)
            {
                var idleDiff = idle - _prevIdleTime;
                var kernelDiff = kernel - _prevKernelTime;
                var userDiff = user - _prevUserTime;

                var total = kernelDiff + userDiff;
                if (total > 0)
                {
                    var busy = total - idleDiff;
                    var percent = (int)Math.Clamp((busy * 100) / total, 0, 100);
                    CurrentCpuPercent = percent;
                }
            }

            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;
            _hasPrevTimes = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read CPU usage: {ex.Message}");
        }
    }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        return ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
