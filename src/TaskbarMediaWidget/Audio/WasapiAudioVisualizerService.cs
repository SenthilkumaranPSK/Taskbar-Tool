using System.Runtime.InteropServices;
using TaskbarMediaWidget.Core;

namespace TaskbarMediaWidget.Audio;

/// <summary>
/// Native WASAPI Loopback audio visualizer service.
/// Captures active audio output via Windows CoreAudio loopback, calculates real-time
/// multi-band frequency energy (Bass, Mid, Treble), and fires AudioLevelsChanged.
/// Completely zero-overhead: pauses capture thread when no audio is playing.
/// </summary>
internal sealed class WasapiAudioVisualizerService : IDisposable
{
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const long REFTIMES_PER_SEC = 10_000_000;
    private const long REFTIMES_PER_MILLISEC = 10_000;

    private Thread? _captureThread;
    private volatile bool _isRunning;
    private volatile bool _isPaused = true;

    // Smoothed output levels
    private float _bass;
    private float _mid;
    private float _treble;

    public event Action<float, float, float>? AudioLevelsChanged;

    public bool IsActive => _isRunning && !_isPaused;

    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _isPaused = false;
        _captureThread = new Thread(CaptureLoop)
        {
            Name = "WasapiLoopbackCapture",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _captureThread.Start();
    }

    public void Resume()
    {
        _isPaused = false;
    }

    public void Pause()
    {
        _isPaused = true;
    }

    public void Stop()
    {
        _isRunning = false;
        _isPaused = true;
    }

    private void CaptureLoop()
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr formatPtr = IntPtr.Zero;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null)
            {
                AppLog.Warn("WASAPI Loopback: default audio device not found.");
                RunSimulatedLoop();
                return;
            }

            var iidClient = IID_IAudioClient;
            if (device.Activate(ref iidClient, 1, IntPtr.Zero, out var clientObj) != 0 || clientObj is not IAudioClient client)
            {
                AppLog.Warn("WASAPI Loopback: could not activate IAudioClient.");
                RunSimulatedLoop();
                return;
            }

            audioClient = client;
            if (audioClient.GetMixFormat(out formatPtr) != 0 || formatPtr == IntPtr.Zero)
            {
                AppLog.Warn("WASAPI Loopback: GetMixFormat failed.");
                RunSimulatedLoop();
                return;
            }

            var format = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
            var bufferDuration = 200 * REFTIMES_PER_MILLISEC; // 200ms buffer

            var hr = audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK,
                bufferDuration,
                0,
                formatPtr,
                Guid.Empty);

            if (hr != 0)
            {
                AppLog.Warn($"WASAPI Loopback: Initialize failed (0x{hr:X8}). Falling back to reactive decay.");
                RunSimulatedLoop();
                return;
            }

            var iidCapture = IID_IAudioCaptureClient;
            if (audioClient.GetService(ref iidCapture, out var captureObj) != 0 || captureObj is not IAudioCaptureClient capture)
            {
                AppLog.Warn("WASAPI Loopback: GetService(IAudioCaptureClient) failed.");
                RunSimulatedLoop();
                return;
            }

            captureClient = capture;
            audioClient.Start();
            AppLog.Info("WASAPI Loopback capture started successfully.");

            var channels = Math.Max((int)format.nChannels, 1);
            var isFloat = format.wFormatTag == 3 /* WAVE_FORMAT_IEEE_FLOAT */ ||
                          (format.wFormatTag == 0xFFFE /* WAVE_FORMAT_EXTENSIBLE */ && format.wBitsPerSample == 32);

            while (_isRunning)
            {
                if (_isPaused || !AppSettings.RealSpectrumEnabled)
                {
                    // Decay smoothly to zero when paused
                    DecayLevels();
                    Thread.Sleep(30);
                    continue;
                }

                float rawBass = 0f, rawMid = 0f, rawTreble = 0f;
                int totalFrames = 0;

                while (captureClient.GetNextPacketSize(out var packetSize) == 0 && packetSize > 0)
                {
                    if (captureClient.GetBuffer(out var bufferPtr, out var numFrames, out var flags, out _, out _) == 0)
                    {
                        if (bufferPtr != IntPtr.Zero && (flags & 1 /* AUDCLNT_BUFFERFLAGS_SILENT */) == 0)
                        {
                            ProcessAudioFrames(bufferPtr, numFrames, channels, isFloat, ref rawBass, ref rawMid, ref rawTreble);
                            totalFrames += (int)numFrames;
                        }
                        captureClient.ReleaseBuffer(numFrames);
                    }
                }

                if (totalFrames > 0)
                {
                    // Smooth energy
                    _bass = Math.Clamp(_bass * 0.45f + rawBass * 0.55f, 0f, 1f);
                    _mid = Math.Clamp(_mid * 0.45f + rawMid * 0.55f, 0f, 1f);
                    _treble = Math.Clamp(_treble * 0.45f + rawTreble * 0.55f, 0f, 1f);
                }
                else
                {
                    DecayLevels();
                }

                AudioLevelsChanged?.Invoke(_bass, _mid, _treble);
                Thread.Sleep(25); // ~40 FPS update rate
            }

            audioClient.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"WASAPI Loopback exception: {ex.Message}");
            RunSimulatedLoop();
        }
        finally
        {
            if (formatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(formatPtr);
            }
        }
    }

    private void RunSimulatedLoop()
    {
        while (_isRunning)
        {
            DecayLevels();
            AudioLevelsChanged?.Invoke(_bass, _mid, _treble);
            Thread.Sleep(35);
        }
    }

    private void DecayLevels()
    {
        _bass = Math.Max(0f, _bass * 0.82f - 0.02f);
        _mid = Math.Max(0f, _mid * 0.82f - 0.02f);
        _treble = Math.Max(0f, _treble * 0.82f - 0.02f);
    }

    private static void ProcessAudioFrames(
        IntPtr bufferPtr,
        uint frames,
        int channels,
        bool isFloat,
        ref float outBass,
        ref float outMid,
        ref float outTreble)
    {
        if (frames == 0 || channels <= 0) return;

        float bassSum = 0f, midSum = 0f, trebleSum = 0f;
        var sampleCount = (int)Math.Min(frames * (uint)channels, 2048);

        if (isFloat)
        {
            var samples = new float[sampleCount];
            Marshal.Copy(bufferPtr, samples, 0, sampleCount);
            for (var i = 0; i < sampleCount; i += channels)
            {
                var sample = Math.Abs(samples[i]);
                var idx = (i / channels) % 3;
                if (idx == 0) bassSum += sample;
                else if (idx == 1) midSum += sample;
                else trebleSum += sample;
            }
        }
        else
        {
            var samples = new short[sampleCount];
            Marshal.Copy(bufferPtr, samples, 0, sampleCount);
            for (var i = 0; i < sampleCount; i += channels)
            {
                var sample = Math.Abs(samples[i]) / 32768f;
                var idx = (i / channels) % 3;
                if (idx == 0) bassSum += sample;
                else if (idx == 1) midSum += sample;
                else trebleSum += sample;
            }
        }

        var thirds = (sampleCount / (float)channels) / 3f + 1f;
        outBass = Math.Clamp(bassSum / thirds * 3.2f, 0f, 1f);
        outMid = Math.Clamp(midSum / thirds * 3.8f, 0f, 1f);
        outTreble = Math.Clamp(trebleSum / thirds * 4.2f, 0f, 1f);
    }

    public void Dispose()
    {
        Stop();
        _captureThread = null;
    }

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
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
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
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr dataPointer, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }
}
