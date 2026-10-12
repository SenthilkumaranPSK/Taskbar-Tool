using System.Runtime.InteropServices;

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

    public void Dispose()
    {
        _endpointVolume = null;
        _initialized = false;
    }

    // COM Interop definitions
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr client);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr client);
        [PreserveSig] int GetChannelCount(out int channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
    }
}
