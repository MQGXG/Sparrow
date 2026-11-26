using System.Buffers;
using System.Runtime.InteropServices;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// WASAPI 音频采集：系统声音（loopback）+ 麦克风。
///
/// 与 LumaShot 的做法一致：固定 48kHz / 16-bit / 立体声，
/// 用 <c>AUTOCONVERTPCM | SRC_DEFAULT_QUALITY</c> 让系统负责重采样，
/// 避免设备混音格式（44.1kHz / float32）不同导致 ffmpeg 拿到错格式。
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BytesPerSample = 2;

    private const int AudioRender = 0;
    private const int AudioCaptureDevice = 1;
    private const int RoleConsole = 0;
    private const int ShareModeShared = 0;

    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsAutoConvert = 0x80000000;
    private const uint StreamFlagsSrcDefaultQuality = 0x08000000;

    private const uint BufferFlagsSilent = 0x00000002;

    private readonly List<Source> _sources = [];
    private bool _started;
    private bool _disposed;

    /// <summary>混音用的可复用缓冲，避免每个视频帧都分配一份 int[样本数]。</summary>
    private int[] _mixBuffer = [];

    /// <summary>每个视频帧需要的音频样本数（stereo 交错）。</summary>
    public static int SamplesPerFrame(int framesPerSecond) =>
        SampleRate / Math.Max(1, framesPerSecond) * Channels;

    /// <summary>
    /// 创建采集器。<paramref name="system"/> 系统声音，<paramref name="microphone"/> 麦克风。
    /// 两者都关闭时返回 null（无音频录制）。
    /// </summary>
    public static AudioCapture? TryCreate(bool system, bool microphone, out string? error)
    {
        error = null;

        if (!system && !microphone)
            return null;

        try
        {
            var capture = new AudioCapture();

            if (system)
                capture.AddSource(AudioRender, loopback: true);

            if (microphone)
                capture.AddSource(AudioCaptureDevice, loopback: false);

            return capture;
        }
        catch (Exception ex)
        {
            error = $"无法初始化音频设备：{ex.Message}";
            return null;
        }
    }

    private void AddSource(int dataFlow, bool loopback)
    {
        var enumerator = (IMMDeviceEnumerator)new MmDeviceEnumeratorComObject();

        try
        {
            Check(
                enumerator.GetDefaultAudioEndpoint(dataFlow, RoleConsole, out var device),
                "GetDefaultAudioEndpoint");

            try
            {
                var audioClientId = typeof(IAudioClient).GUID;
                Check(
                    device.Activate(ref audioClientId, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var clientObject),
                    "Activate(IAudioClient)");

                var client = (IAudioClient)clientObject;

                try
                {
                    var format = new WaveFormatEx
                    {
                        FormatTag = 1, // WAVE_FORMAT_PCM
                        Channels = Channels,
                        SamplesPerSec = SampleRate,
                        AvgBytesPerSec = SampleRate * Channels * BytesPerSample,
                        BlockAlign = Channels * BytesPerSample,
                        BitsPerSample = BytesPerSample * 8,
                        Size = 0
                    };

                    var formatPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());

                    try
                    {
                        Marshal.StructureToPtr(format, formatPointer, false);

                        var flags = StreamFlagsAutoConvert | StreamFlagsSrcDefaultQuality;
                        if (loopback)
                            flags |= StreamFlagsLoopback;

                        // 100ms 缓冲；共享模式下 periodicity 必须为 0。
                        Check(
                            client.Initialize(
                                ShareModeShared,
                                unchecked((int)flags),
                                1_000_000,
                                0,
                                formatPointer,
                                IntPtr.Zero),
                            "IAudioClient.Initialize");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(formatPointer);
                    }

                    var captureId = typeof(IAudioCaptureClient).GUID;
                    Check(
                        client.GetService(ref captureId, out var captureObject),
                        "GetService(IAudioCaptureClient)");

                    _sources.Add(new Source(client, (IAudioCaptureClient)captureObject));
                }
                catch
                {
                    ReleaseCom(client);
                    throw;
                }
            }
            finally
            {
                ReleaseCom(device);
            }
        }
        finally
        {
            ReleaseCom(enumerator);
        }
    }

    public void Start()
    {
        if (_started)
            return;

        foreach (var source in _sources)
            Check(source.Client.Start(), "IAudioClient.Start");

        _started = true;
    }

    /// <summary>
    /// 读取并混音一组样本（stereo 交错）。没有数据时输出静音。
    /// 必须是 48kHz 下的样本数，否则会与视频时长漂移。
    /// </summary>
    public void Read(Span<short> destination)
    {
        foreach (var source in _sources)
            Drain(source);

        if (_mixBuffer.Length < destination.Length)
            _mixBuffer = new int[destination.Length];

        var mix = _mixBuffer.AsSpan(0, destination.Length);
        mix.Clear();

        var any = false;

        foreach (var source in _sources)
        {
            if (source.Samples.Count == 0)
                continue;

            any = true;

            for (var i = 0; i < destination.Length && source.Samples.Count > 0; i++)
                mix[i] += source.Samples.Dequeue();
        }

        if (!any)
        {
            destination.Clear();
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = (short)Math.Clamp(mix[i], short.MinValue, short.MaxValue);
    }

    private static void Drain(Source source)
    {
        Check(source.Capture.GetNextPacketSize(out var packetFrames), "GetNextPacketSize");

        while (packetFrames > 0)
        {
            Check(
                source.Capture.GetBuffer(
                    out var data,
                    out var frames,
                    out var flags,
                    out _,
                    out _),
                "IAudioCaptureClient.GetBuffer");

            try
            {
                if (frames > 0 && data != IntPtr.Zero)
                {
                    var sampleCount = (int)frames * Channels;

                    // 最多缓存 500ms，超出说明消费端落后，丢弃旧数据防止无限增长。
                    if (source.Samples.Count + sampleCount <= SampleRate * Channels / 2)
                    {
                        if ((flags & BufferFlagsSilent) != 0)
                        {
                            for (var i = 0; i < sampleCount; i++)
                                source.Samples.Enqueue(0);
                        }
                        else
                        {
                            var buffer = ArrayPool<short>.Shared.Rent(sampleCount);

                            try
                            {
                                Marshal.Copy(data, buffer, 0, sampleCount);

                                for (var i = 0; i < sampleCount; i++)
                                    source.Samples.Enqueue(buffer[i]);
                            }
                            finally
                            {
                                ArrayPool<short>.Shared.Return(buffer);
                            }
                        }
                    }
                }
            }
            finally
            {
                Check(source.Capture.ReleaseBuffer(frames), "ReleaseBuffer");
            }

            Check(source.Capture.GetNextPacketSize(out packetFrames), "GetNextPacketSize");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var source in _sources)
        {
            try
            {
                if (_started)
                    source.Client.Stop();
            }
            catch
            {
                // 设备可能已被拔出。
            }

            ReleaseCom(source.Capture);
            ReleaseCom(source.Client);
        }

        _sources.Clear();
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0)
            throw new COMException($"{operation} 失败（HRESULT 0x{hr:X8}）", hr);
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }

    private sealed record Source(IAudioClient Client, IAudioCaptureClient Capture)
    {
        public Queue<short> Samples { get; } = new();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MmDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(
            ref Guid iid,
            int classContext,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(
            int shareMode,
            int streamFlags,
            long bufferDuration,
            long periodicity,
            IntPtr format,
            IntPtr audioSessionGuid);

        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);

        [PreserveSig] int GetService(
            ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(
            out IntPtr data,
            out uint frames,
            out uint flags,
            out long devicePosition,
            out long qpcPosition);

        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
