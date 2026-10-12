using System.Runtime.InteropServices;

namespace TaskbarMediaWidget.Interop;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigComObject { }

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate([In] ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[Guid("886D8EEB-8CF2-4446-8D02-C33284CD4D87"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
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

[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr waveFormat, [In] Guid audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr waveFormat, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormatPointer);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
}

[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr dataPointer, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

[Guid("f8679f50-850a-4129-9dc1-69e6386ac8fb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
    [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
    [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
    [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
    [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
    [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
    [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
    [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
    [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
    [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, int role);
    [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public short vt;
    [FieldOffset(8)] public IntPtr ptrVal;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}
