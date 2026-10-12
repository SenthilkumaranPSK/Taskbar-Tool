using System.Runtime.InteropServices;
using TaskbarMediaWidget.Interop;

namespace TaskbarMediaWidget.Core;

/// <summary>
/// Native Windows CoreAudio Master Volume service using IAudioEndpointVolume COM interface.
/// Provides real-time master volume reading, writing, and mute control without simulated keystrokes.
/// </summary>
internal sealed class CoreAudioVolumeService : IDisposable
{
    private IAudioEndpointVolume? _endpointVolume;
    private bool _initialized;

    public event Action<int, bool>? VolumeChanged;

    public CoreAudioVolumeService()
    {
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) == 0 && device is not null)
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 1, IntPtr.Zero, out var obj) == 0 && obj is IAudioEndpointVolume volume)
                {
                    _endpointVolume = volume;
                    _initialized = true;
                    AppLog.Info("CoreAudio volume service initialized successfully.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"CoreAudio initialization failed: {ex.Message}");
            _initialized = false;
        }
    }

    public int GetVolumePercent()
    {
        if (!_initialized || _endpointVolume is null)
        {
            return 50;
        }

        try
        {
            if (_endpointVolume.GetMasterVolumeLevelScalar(out var level) == 0)
            {
                return (int)Math.Round(Math.Clamp(level * 100f, 0f, 100f));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to read master volume: {ex.Message}");
        }

        return 50;
    }

    public bool SetVolumePercent(int percent)
    {
        if (!_initialized || _endpointVolume is null)
        {
            return false;
        }

        try
        {
            var scalar = Math.Clamp(percent / 100f, 0f, 1f);
            var context = Guid.Empty;
            var hr = _endpointVolume.SetMasterVolumeLevelScalar(scalar, ref context);
            if (hr == 0)
            {
                VolumeChanged?.Invoke(percent, IsMuted());
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to set master volume: {ex.Message}");
        }

        return false;
    }

    public bool IsMuted()
    {
        if (!_initialized || _endpointVolume is null)
        {
            return false;
        }

        try
        {
            if (_endpointVolume.GetMute(out var isMuted) == 0)
            {
                return isMuted;
            }
        }
        catch
        {
            // fallback
        }

        return false;
    }

    public bool SetMute(bool isMuted)
    {
        if (!_initialized || _endpointVolume is null)
        {
            return false;
        }

        try
        {
            var context = Guid.Empty;
            if (_endpointVolume.SetMute(isMuted, ref context) == 0)
            {
                VolumeChanged?.Invoke(GetVolumePercent(), isMuted);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to set mute state: {ex.Message}");
        }

        return false;
    }

    public bool ToggleMute()
    {
        return SetMute(!IsMuted());
    }

    public void VolumeUp(int stepPercent = 2)
    {
        var current = GetVolumePercent();
        var next = Math.Min(100, current + stepPercent);
        SetVolumePercent(next);
        if (IsMuted())
        {
            SetMute(false);
        }
    }

    public void VolumeDown(int stepPercent = 2)
    {
        var current = GetVolumePercent();
        var next = Math.Max(0, current - stepPercent);
        SetVolumePercent(next);
    }

    public record AudioDeviceItem(string Id, string Name, bool IsDefault);

    public IReadOnlyList<AudioDeviceItem> GetAudioPlaybackDevices()
    {
        var list = new List<AudioDeviceItem>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var defDevice) == 0 && defDevice is not null)
            {
                defDevice.GetId(out defaultId);
            }

            if (enumerator.EnumAudioEndpoints(0, 1, out var pCollection) == 0 && pCollection != IntPtr.Zero)
            {
                var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(pCollection);
                collection.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) == 0 && device is not null)
                    {
                        device.GetId(out var id);
                        var name = id;
                        if (device.OpenPropertyStore(0, out var pStore) == 0 && pStore != IntPtr.Zero)
                        {
                            var store = (IPropertyStore)Marshal.GetObjectForIUnknown(pStore);
                            var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                            if (store.GetValue(ref key, out var pv) == 0 && pv.ptrVal != IntPtr.Zero)
                            {
                                name = Marshal.PtrToStringUni(pv.ptrVal) ?? id;
                            }
                        }
                        list.Add(new AudioDeviceItem(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to list audio devices: {ex.Message}");
        }
        return list;
    }

    public bool SwitchDefaultAudioDevice(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigComObject();
            policy.SetDefaultEndpoint(deviceId, 0); // eConsole
            policy.SetDefaultEndpoint(deviceId, 1); // eMultimedia
            policy.SetDefaultEndpoint(deviceId, 2); // eCommunications
            Initialize();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to switch default audio endpoint: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _endpointVolume = null;
        _initialized = false;
    }
}
