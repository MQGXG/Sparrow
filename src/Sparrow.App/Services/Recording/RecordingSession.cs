using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sparrow.App.Services.Recording;

internal enum RecordingState
{
    Idle,
    Preparing,
    Recording,
    Paused,
    Stopping,
    Completed,
    Faulted
}

/// <summary>
/// 一次录制会话：WGC 帧源 → H.264 编码 → MP4 文件。
///
/// 宿主能力，不进插件体系（docs/plugin-model.md 第十二节）。
/// 会话在后台线程跑，开始/停止由宿主的热键与控制条驱动；
/// 编码是流式的，不把整段视频攒在内存里。
/// </summary>
internal sealed class RecordingSession : IDisposable
{
    private readonly RecordingOptions _options;
    private readonly WgcFrameSource _source;
    private readonly FfmpegEncoder _encoder;
    private readonly AudioCapture? _audio;
    private readonly WavWriter? _wav;
    private readonly CursorHighlighter? _highlighter;
    private readonly WatermarkRenderer? _watermark;
    private readonly string _videoPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly Stopwatch _clock = new();
    private int _disposed;
    private volatile bool _paused;
    private volatile bool _resumeReset;

    /// <summary>状态变化通知。在后台线程触发，UI 订阅方需要自己切回 UI 线程。</summary>
    public event Action<RecordingState, string?>? StateChanged;

    public RecordingState State { get; private set; } = RecordingState.Idle;

    /// <summary>最近一次失败原因（停止失败时用于提示）。</summary>
    public string? LastError { get; private set; }

    /// <summary>已写入编码器的帧数。probe 用它判断是「没采到帧」还是「编码失败」。</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>实际使用的帧率（可能被分辨率上限压低）。</summary>
    public int FramesPerSecond => _options.FramesPerSecond;

    public string OutputPath => _options.OutputPath;

    /// <summary>实际使用的视频编码器（h264_qsv / h264_nvenc / h264_amf / h264_mf / libx264）。</summary>
    public string EncoderName => _encoder.EncoderName;

    /// <summary>本次是否在录系统声音（控制条显示用）。</summary>
    public bool HasSystemAudio => _options.SystemAudio;

    /// <summary>本次是否在录麦克风（控制条显示用）。</summary>
    public bool HasMicrophone => _options.Microphone;

    private long _framesWritten;

    private RecordingSession(
        RecordingOptions options,
        WgcFrameSource source,
        FfmpegEncoder encoder,
        AudioCapture? audio,
        WavWriter? wav,
        CursorHighlighter? highlighter,
        WatermarkRenderer? watermark,
        string videoPath)
    {
        _options = options;
        _source = source;
        _encoder = encoder;
        _audio = audio;
        _wav = wav;
        _highlighter = highlighter;
        _watermark = watermark;
        _videoPath = videoPath;
        _worker = Task.Run(Run);
    }

    /// <summary>创建并启动会话。失败返回 null，<paramref name="error"/> 是可直接展示的原因。</summary>
    public static RecordingSession? TryStart(RecordingOptions options, out string? error)
    {
        error = null;

        var directory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                error = $"无法创建保存目录：{ex.Message}";
                return null;
            }
        }

        var source = options.WindowHandle != IntPtr.Zero
            ? WgcFrameSource.TryCreateForWindow(
                options.WindowHandle,
                options.IncludeCursor,
                out error)
            : WgcFrameSource.TryCreate(
                options.Region,
                options.IncludeCursor,
                out error);

        if (source is null)
            return null;

        // 分辨率越高，能稳定编码的帧率越低：超了会丢帧、视频时长被压缩。
        var maxFps = MaxFramesPerSecond(source.Width, source.Height);
        if (options.FramesPerSecond > maxFps)
            options = options with { FramesPerSecond = maxFps };

        // 视频先写临时文件；有音频时等停止再合并成最终文件。
        // 容器跟输出格式走：MKV/MOV 让 ffmpeg 按扩展名选封装器。
        var container = options.Format is "mkv" or "mov" ? options.Format : "mp4";
        var videoPath = options.OutputPath + ".video." + container;

        var encoder = FfmpegEncoder.TryCreate(
            videoPath,
            source.Width,
            source.Height,
            options.FramesPerSecond,
            options.Crf,
            options.Preset,
            options.GopSeconds,
            options.Encoder,
            out var encoderError);

        if (encoder is null)
        {
            source.Dispose();
            error = encoderError ?? "无法启动视频编码。";
            return null;
        }

        // 音频初始化失败不阻止录制：降级为无声视频。
        var audio = AudioCapture.TryCreate(
            options.SystemAudio,
            options.Microphone,
            out _);

        WavWriter? wav = null;

        if (audio is not null)
        {
            try
            {
                wav = new WavWriter(options.OutputPath + ".wav");
            }
            catch
            {
                audio.Dispose();
                audio = null;
            }
        }

        return new RecordingSession(
            options,
            source,
            encoder,
            audio,
            wav,
            options.IncludeCursor
                ? new CursorHighlighter(
                    options.CursorRadius,
                    options.CursorColor,
                    options.CursorOpacity,
                    options.CursorShadow)
                : null,
            options.Watermark is { IsEmpty: false } watermark
                ? new WatermarkRenderer(watermark)
                : null,
            videoPath);
    }

    /// <summary>暂停录制：停止取帧与计时，视频里不包含暂停时段。</summary>
    public void Pause()
    {
        if (State != RecordingState.Recording)
            return;

        _paused = true;
        _clock.Stop();
        SetState(RecordingState.Paused, null);
    }

    /// <summary>继续录制。</summary>
    public void Resume()
    {
        if (State != RecordingState.Paused)
            return;

        _paused = false;
        _resumeReset = true;
        _clock.Start();
        SetState(RecordingState.Recording, null);
    }

    /// <summary>停止录制并返回文件路径；失败返回 null（原因通过 StateChanged 给出）。</summary>
    public async Task<string?> StopAsync()
    {
        if (State is RecordingState.Completed)
            return _options.OutputPath;

        if (State is RecordingState.Faulted)
            return null;

        SetState(RecordingState.Stopping, null);

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SetState(RecordingState.Faulted, "停止录制超时，文件可能不完整。");
            return null;
        }

        if (State != RecordingState.Completed)
            return null;

        try
        {
            await Task.Run(FinalizeOutput).ConfigureAwait(false);
            return _options.OutputPath;
        }
        catch (Exception ex)
        {
            SetState(RecordingState.Faulted, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 有音频时把无声视频和 WAV 合并成最终 MP4；没有音频直接改名。
    /// 临时文件最后统一清理。
    /// </summary>
    private void FinalizeOutput()
    {
        var audioPath = _options.OutputPath + ".wav";

        try
        {
            if (_options.Format == "gif")
            {
                FfmpegEncoder.ToGif(
                    _videoPath,
                    _options.OutputPath,
                    _options.GifFps,
                    _options.GifWidth);

                return;
            }

            if (_options.Format == "apng")
            {
                FfmpegEncoder.ToApng(
                    _videoPath,
                    _options.OutputPath,
                    _options.GifFps,
                    _options.GifWidth);

                return;
            }

            if (File.Exists(audioPath) && new FileInfo(audioPath).Length > 44)
            {
                FfmpegEncoder.Merge(_videoPath, audioPath, _options.OutputPath);
            }
            else
            {
                File.Move(_videoPath, _options.OutputPath, overwrite: true);
            }
        }
        catch
        {
            // 合并失败时保住无声视频，至少不要丢录制内容。
            if (File.Exists(_videoPath))
                File.Move(_videoPath, _options.OutputPath, overwrite: true);

            throw;
        }
        finally
        {
            TryDelete(_videoPath);
            TryDelete(audioPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ffmpeg 可能还没完全释放文件句柄，下次启动时再清。
        }
    }

    /// <summary>把系统定时器精度提到 1ms，否则 Sleep(1) 实际约 15ms，60fps 以上会掉帧。</summary>
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    /// <summary>按像素数限制可稳定编码的帧率；超了只会丢帧并压缩视频时长。</summary>
    private static int MaxFramesPerSecond(int width, int height)
    {
        var pixels = (long)width * height;

        return pixels switch
        {
            <= 1280L * 720 => 144,
            <= 1920L * 1080 => 120,
            <= 2560L * 1440 => 60,
            // 4K 在常见核显/中端独显上 60fps 会持续掉帧，30 才能保证时长准确。
            _ => 30
        };
    }

    private void Run()
    {
        timeBeginPeriod(1);
        _clock.Start();
        SetState(RecordingState.Recording, null);

        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _options.FramesPerSecond));
        var nextDue = TimeSpan.Zero;
        byte[]? lastPixels = null;

        // 音频采样缓冲：每个视频帧写一组，保证音画时长一致。
        var audioSamples = _audio is not null
            ? new short[AudioCapture.SamplesPerFrame(_options.FramesPerSecond)]
            : null;

        if (audioSamples is not null)
        {
            try
            {
                _audio!.Start();
            }
            catch
            {
                // 设备被占用/拔出：降级为无声录制，视频继续。
                audioSamples = null;
            }
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_paused)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // 录制时长上限：到点自动收尾，等同用户按停止。
                if (_options.MaxDuration is { } max && _clock.Elapsed >= max)
                    break;

                if (_source.TryGetFrame(out var frame))
                    lastPixels = frame.Pixels;

                var now = _clock.Elapsed;

                // 暂停恢复后时间轴从当前点继续，不补写暂停期间的帧。
                if (_resumeReset)
                {
                    nextDue = now;
                    _resumeReset = false;
                }

                // 静止画面时 WGC 不一定给新帧，重复上一帧保证视频时长不被压缩。
                if (now >= nextDue && lastPixels is not null)
                {
                    // 先水印（固定），再光标（会动）：两者都改同一块 BGRA。
                    _watermark?.Draw(lastPixels, _source.Width, _source.Height);

                    _highlighter?.Draw(
                        lastPixels,
                        _source.Width,
                        _source.Height,
                        _source.ScreenRect,
                        now);

                    _encoder.WriteFrame(lastPixels);

                    if (audioSamples is not null)
                    {
                        _audio!.Read(audioSamples);
                        _wav!.Write(audioSamples);
                    }

                    Interlocked.Increment(ref _framesWritten);
                    nextDue += interval;

                    // 卡顿超过 1 秒才重置时间轴；普通掉帧继续追帧，
                    // 避免把视频时长压短（高帧率时尤其明显）。
                    if (nextDue < now - TimeSpan.FromSeconds(1))
                        nextDue = now;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            _encoder.Complete();
            SetState(RecordingState.Completed, null);
        }
        catch (Exception ex)
        {
            SetState(RecordingState.Faulted, ex.ToString());
        }
        finally
        {
            timeEndPeriod(1);

            try
            {
                _wav?.Complete();
            }
            catch
            {
                // WAV 收尾失败不影响已完成的视频。
            }
        }
    }

    private void SetState(RecordingState state, string? error)
    {
        State = state;

        if (error is not null)
            LastError = error;

        try
        {
            StateChanged?.Invoke(state, error);
        }
        catch
        {
            // 订阅方（UI）的异常不能把录制线程带崩。
        }
    }

    /// <summary>丢弃当前录制：停止并删除文件。</summary>
    public void Abandon()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _cts.Cancel();
            _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 停止失败也要尽量释放原生资源。
        }

        // 逐项释放：任何一个原生资源抛异常都不能跳过后面的释放，
        // 尤其是 _source（WGC 帧池 + 33MB LOH 帧缓冲）。
        DisposeQuietly(_encoder.Abandon);
        DisposeQuietly(() => { _audio?.Dispose(); });
        DisposeQuietly(() => { _wav?.Dispose(); });
        DisposeQuietly(() => { _watermark?.Dispose(); });
        DisposeQuietly(_source.Dispose);
        DisposeQuietly(_cts.Dispose);

        TryDelete(_videoPath);
        TryDelete(_options.OutputPath + ".wav");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _cts.Cancel();
            _worker.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 停止失败也要尽量释放原生资源。
        }

        // 失败路径不再调 finalize：没有完整帧序时 DoFinalize 可能长时间阻塞。
        if (State == RecordingState.Faulted)
            DisposeQuietly(_encoder.Abandon);
        else
            DisposeQuietly(_encoder.Dispose);

        DisposeQuietly(() => { _audio?.Dispose(); });
        DisposeQuietly(() => { _wav?.Dispose(); });
        DisposeQuietly(() => { _watermark?.Dispose(); });
        DisposeQuietly(_source.Dispose);
        DisposeQuietly(_cts.Dispose);

        // 正常完成后 FinalizeOutput 已清理；失败/取消时兜底删除临时文件。
        TryDelete(_videoPath);
        TryDelete(_options.OutputPath + ".wav");
    }

    /// <summary>
    /// 单项释放失败不阻断其余项。原实现里 _encoder / _audio 若抛异常，
    /// 后面的 _source.Dispose() 会被整段跳过，WGC 会话、帧池与 33MB
    /// 帧缓冲就常驻到进程退出（录屏失败后 LOH 高水位不回落的原因）。
    /// </summary>
    private static void DisposeQuietly(Action dispose)
    {
        try
        {
            dispose();
        }
        catch
        {
            // 忽略单项失败，继续释放剩余资源。
        }
    }
}
