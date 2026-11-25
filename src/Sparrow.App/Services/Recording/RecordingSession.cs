using System.Diagnostics;

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

    public string OutputPath => _options.OutputPath;

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

        // 视频先写临时文件；有音频时等停止再合并成最终 MP4。
        var videoPath = options.OutputPath + ".video.mp4";

        var encoder = FfmpegEncoder.TryCreate(
            videoPath,
            source.Width,
            source.Height,
            options.FramesPerSecond,
            options.Crf,
            options.Preset,
            options.GopSeconds,
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
            if (_options.Gif)
            {
                FfmpegEncoder.ToGif(
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

    private void Run()
    {
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

                    // 编码落后超过一帧就跳过去，不补写（补写会让画面突然加速）。
                    if (nextDue < now - interval)
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

        _encoder.Abandon();
        _audio?.Dispose();
        _wav?.Dispose();
        _watermark?.Dispose();
        _source.Dispose();
        _cts.Dispose();

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
            _encoder.Abandon();
        else
            _encoder.Dispose();

        _audio?.Dispose();
        _wav?.Dispose();
        _watermark?.Dispose();
        _source.Dispose();
        _cts.Dispose();

        // 正常完成后 FinalizeOutput 已清理；失败/取消时兜底删除临时文件。
        TryDelete(_videoPath);
        TryDelete(_options.OutputPath + ".wav");
    }
}
