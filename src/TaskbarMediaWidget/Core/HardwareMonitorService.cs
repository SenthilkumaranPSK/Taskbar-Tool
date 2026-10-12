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

    private long _prevBytesReceived;
    private long _prevBytesSent;
    private DateTime _prevNetworkTime = DateTime.MinValue;
    private bool _hasPrevNetwork;

    public event Action<int, int>? UsageUpdated;
    public event Action<HardwareStats>? StatsUpdated;

    public int CurrentCpuPercent { get; private set; }
    public int CurrentRamPercent { get; private set; }
    public double DownloadSpeedKbps { get; private set; }
    public double UploadSpeedKbps { get; private set; }
    public int? BatteryPercent { get; private set; }
    public bool? IsCharging { get; private set; }

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
        UpdateNetworkUsage();
        UpdateBatteryStatus();

        UsageUpdated?.Invoke(CurrentCpuPercent, CurrentRamPercent);
        StatsUpdated?.Invoke(new HardwareStats(
            CurrentCpuPercent,
            CurrentRamPercent,
            DownloadSpeedKbps,
            UploadSpeedKbps,
            BatteryPercent,
            IsCharging));
    }

    private void UpdateBatteryStatus()
    {
        try
        {
            var power = System.Windows.Forms.SystemInformation.PowerStatus;
            if (power.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
            {
                BatteryPercent = (int)Math.Round(power.BatteryLifePercent * 100);
                IsCharging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            }
            else
            {
                BatteryPercent = null;
                IsCharging = null;
            }
        }
        catch
        {
            BatteryPercent = null;
            IsCharging = null;
        }
    }

    private void UpdateNetworkUsage()
    {
        try
        {
            long rx = 0;
            long tx = 0;
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    var stats = nic.GetIPv4Statistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
            }

            var now = DateTime.UtcNow;
            if (_hasPrevNetwork && _prevNetworkTime != DateTime.MinValue)
            {
                var seconds = (now - _prevNetworkTime).TotalSeconds;
                if (seconds > 0)
                {
                    var rxDiff = Math.Max(0, rx - _prevBytesReceived);
                    var txDiff = Math.Max(0, tx - _prevBytesSent);
                    DownloadSpeedKbps = (rxDiff / seconds) / 1024.0;
                    UploadSpeedKbps = (txDiff / seconds) / 1024.0;
                }
            }

            _prevBytesReceived = rx;
            _prevBytesSent = tx;
            _prevNetworkTime = now;
            _hasPrevNetwork = true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read network stats: {ex.Message}");
        }
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

public sealed record HardwareStats(
    int CpuPercent,
    int RamPercent,
    double DownloadSpeedKbps,
    double UploadSpeedKbps,
    int? BatteryPercent,
    bool? IsCharging);
