using Avalonia;
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
    private readonly CameraCapture? _camera;
    private readonly object _cameraGate = new();
    private PixelRect _cameraScreenRect;
    private PixelRect _cameraLocalRect;
    private readonly string _videoPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly Stopwatch _clock = new();
    private int _disposed;
    private volatile bool _paused;
    private volatile bool _resumeReset;
    private volatile bool _abandoned;

    /// <summary>状态变化通知。在后台线程触发，UI 订阅方需要自己切回 UI 线程。</summary>
    public event Action<RecordingState, string?>? StateChanged;

    public RecordingState State { get; private set; } = RecordingState.Idle;

    /// <summary>最近一次失败原因（停止失败时用于提示）。</summary>
    public string? LastError { get; private set; }

    /// <summary>已写入编码器的帧数。probe 用它判断是「没采到帧」还是「编码失败」。</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public TimeSpan Elapsed => _clock.Elapsed;

    public string OutputPath => _options.OutputPath;

    /// <summary>实际使用的视频编码器（h264_qsv / h264_nvenc / h264_amf / h264_mf / libx264）。</summary>
    public string EncoderName => _encoder.EncoderName;

    /// <summary>本次是否在录系统声音（控制条显示用）。</summary>
    public bool HasSystemAudio => _options.SystemAudio;

    /// <summary>本次是否在录麦克风（控制条显示用）。</summary>
    public bool HasMicrophone => _options.Microphone;

    /// <summary>本次是否启用了摄像头画中画。</summary>
    public CameraCapture? Camera => _camera;

    /// <summary>摄像头画面是否水平镜像（预览和合成保持一致）。</summary>
    public bool CameraMirror => _options.CameraMirror;

    /// <summary>录制区域在屏幕上的物理矩形（摄像头坐标换算用）。</summary>
    public PixelRect ScreenRect => _source.ScreenRect;

    /// <summary>摄像头预览的初始屏幕矩形（物理像素）。</summary>
    public PixelRect InitialCameraScreenRect { get; }

    /// <summary>摄像头预览当前所在的屏幕矩形（物理像素）。</summary>
    public PixelRect CameraScreenRect
    {
        get
        {
            lock (_cameraGate)
                return _cameraScreenRect;
        }
    }

    /// <summary>预览窗口移动/缩放后调用；只保留与录制区域相交的部分用于合成。</summary>
    public void SetCameraScreenRect(PixelRect screenRect)
    {
        if (screenRect.Width <= 0 || screenRect.Height <= 0)
            return;

        lock (_cameraGate)
        {
            _cameraScreenRect = screenRect;
            _cameraLocalRect = MapCameraToLocal(screenRect);
        }
    }

    private long _framesWritten;

    private RecordingSession(
        RecordingOptions options,
        WgcFrameSource source,
        FfmpegEncoder encoder,
        AudioCapture? audio,
        WavWriter? wav,
        CursorHighlighter? highlighter,
        WatermarkRenderer? watermark,
        CameraCapture? camera,
        string videoPath)
    {
        _options = options;
        _source = source;
        _encoder = encoder;
        _audio = audio;
        _wav = wav;
        _highlighter = highlighter;
        _watermark = watermark;
        _camera = camera;
        _videoPath = videoPath;

        InitialCameraScreenRect = options.CameraScreenRect is { Width: > 0, Height: > 0 } provided
            ? provided
            : CameraPlacement.DefaultScreenRect(CameraRegion());
        _cameraScreenRect = InitialCameraScreenRect;
        _cameraLocalRect = MapCameraToLocal(InitialCameraScreenRect);

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

        CameraCapture? camera = null;

        if (options.Camera)
        {
            camera = options.CameraSource ?? CameraCapture.Create();
            camera.Start();
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
            camera,
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

    /// <summary>
    /// 停止录制并返回文件路径；失败返回 null（原因通过 StateChanged 给出）。
    /// 收尾（ffmpeg 关闭、合并音频、改名）在录制线程内完成，
    /// 因此 Completed 状态一出现，输出文件就已经可用。
    /// </summary>
    public async Task<string?> StopAsync()
    {
        if (State is RecordingState.Completed)
            return _options.OutputPath;

        if (State is RecordingState.Faulted)
            return null;

        if (State is not RecordingState.Stopping)
            SetState(RecordingState.Stopping, null);

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            // GIF/APNG 转换和音频合并可能比普通停止慢；收尾在 worker 内完成。
            await _worker.WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SetState(RecordingState.Faulted, "停止录制超时，文件可能不完整。");
            return null;
        }

        if (State is RecordingState.Faulted)
            return null;

        return State is RecordingState.Completed ? _options.OutputPath : null;
    }

    /// <summary>
    /// 有音频时把无声视频和 WAV 合并成最终 MP4；没有音频直接改名。
    /// 临时文件最后统一清理。
    /// </summary>
    private void FinalizeOutput()
    {
        if (_abandoned)
            return;

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

    /// <summary>录制区域：优先用实际抓屏矩形，窗口录制拿不到时退回参数/主屏。</summary>
    private PixelRect CameraRegion()
    {
        var region = _source.ScreenRect;

        if (region.Width > 0 && region.Height > 0)
            return region;

        if (_options.Region.Width > 0 && _options.Region.Height > 0)
            return _options.Region;

        return WgcCapture.GetPrimaryMonitorBounds();
    }

    /// <summary>默认画中画放在右下角，约为录制高度的 1/4，保持 16:9。</summary>
    /// <summary>屏幕物理矩形 → 录制画面内的局部矩形，超出区域的部分裁掉。</summary>
    private PixelRect MapCameraToLocal(PixelRect screenRect)
    {
        var region = CameraRegion();
        var left = Math.Max(screenRect.X, region.X);
        var top = Math.Max(screenRect.Y, region.Y);
        var right = Math.Min(screenRect.Right, region.Right);
        var bottom = Math.Min(screenRect.Bottom, region.Bottom);

        if (right <= left || bottom <= top)
            return default;

        return new PixelRect(left - region.X, top - region.Y, right - left, bottom - top);
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

                    DrawCamera(lastPixels);

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

            // 取消（Abandon）不再做任何收尾，临时文件由 Abandon 删除。
            if (_abandoned)
                return;

            _encoder.Complete();
            _wav?.Complete();

            // 先落盘再发 Completed：UI 收到完成时文件已经是最终文件。
            FinalizeOutput();

            if (_abandoned)
            {
                TryDelete(_options.OutputPath);
                return;
            }

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

    private void DrawCamera(byte[] pixels)
    {
        if (_camera is null)
            return;

        PixelRect local;

        lock (_cameraGate)
            local = _cameraLocalRect;

        if (local.Width <= 0 || local.Height <= 0)
            return;

        _camera.Draw(
            pixels,
            _source.Width,
            _source.Height,
            local,
            _options.CameraMirror);
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
            _abandoned = true;
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
        _camera?.Dispose();
        _source.Dispose();
        _cts.Dispose();

        TryDelete(_videoPath);
        TryDelete(_options.OutputPath + ".wav");
        TryDelete(_options.OutputPath);
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
        _camera?.Dispose();
        _source.Dispose();
        _cts.Dispose();

        // 正常完成后 FinalizeOutput 已清理；失败/取消时兜底删除临时文件。
        TryDelete(_videoPath);
        TryDelete(_options.OutputPath + ".wav");
    }
}
