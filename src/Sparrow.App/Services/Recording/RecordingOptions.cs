using Avalonia;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 屏幕录制参数。
///
/// 录制是宿主能力，不是插件（见 docs/plugin-model.md 第十二节）：
/// 持续帧源、控制条、流式落盘、停止热键都在宿主侧。
/// </summary>
internal sealed record RecordingOptions
{
    /// <summary>录制区域（屏幕物理像素）。窗口录制时为 default，由 <see cref="WindowHandle"/> 决定。</summary>
    public required PixelRect Region { get; init; }

    /// <summary>窗口录制的窗口句柄；为 0 时按 <see cref="Region"/> 录屏幕区域。</summary>
    public IntPtr WindowHandle { get; init; }

    /// <summary>目标帧率。MVP 固定 30；60fps 需要 GPU 纹理直通，见 docs/roadmap.md。</summary>
    public int FramesPerSecond { get; init; } = 30;

    /// <summary>视频码率（bps）。0 = 按分辨率自动估算。当前由 ffmpeg 的 CRF 控制，保留字段供后续码率模式使用。</summary>
    public int Bitrate { get; init; }

    /// <summary>H.264 质量（CRF 16 原画 … 30 一般）。</summary>
    public int Crf { get; init; } = 23;

    /// <summary>ffmpeg 编码 preset：ultrafast / superfast / veryfast / faster / medium。</summary>
    public string Preset { get; init; } = "veryfast";

    /// <summary>
    /// 视频编码器：auto（按显卡自动挑）或具体名字
    /// h264_qsv / h264_nvenc / h264_amf / h264_mf / libx264。
    /// </summary>
    public string Encoder { get; init; } = "auto";

    /// <summary>开始前的倒计时秒数；0 表示立即开始。</summary>
    public int CountdownSeconds { get; init; }

    /// <summary>录制时长上限；null 或 0 表示不限。</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>输出格式：mp4 / mkv / mov（视频）或 gif / apng（无声图片动画）。</summary>
    public string Format { get; init; } = "mp4";

    /// <summary>GIF 帧率（10/15/20）。</summary>
    public int GifFps { get; init; } = 15;

    /// <summary>GIF 最长边（480/720/1080）。</summary>
    public int GifWidth { get; init; } = 720;

    /// <summary>水印布局；null 表示不画水印。</summary>
    public WatermarkLayout? Watermark { get; init; }

    /// <summary>输出文件绝对路径（.mp4）。</summary>
    public required string OutputPath { get; init; }

    /// <summary>是否把鼠标光标画进画面。</summary>
    public bool IncludeCursor { get; init; } = true;

    /// <summary>光标高亮光圈半径（像素）。</summary>
    public int CursorRadius { get; init; } = 22;

    /// <summary>光标高亮颜色（ARGB）。</summary>
    public uint CursorColor { get; init; } = 0xFFFFD640;

    /// <summary>光标高亮透明度（0..255）。</summary>
    public byte CursorOpacity { get; init; } = 210;

    /// <summary>是否给光标光圈加阴影。</summary>
    public bool CursorShadow { get; init; }

    /// <summary>GOP 秒数；0 表示用编码器默认。</summary>
    public int GopSeconds { get; init; }

    /// <summary>录制系统声音（WASAPI loopback）。</summary>
    public bool SystemAudio { get; init; }

    /// <summary>录制麦克风。</summary>
    public bool Microphone { get; init; }

    /// <summary>是否合成摄像头画中画。</summary>
    public bool Camera { get; init; }

    /// <summary>摄像头画面是否水平镜像（自拍视角）。</summary>
    public bool CameraMirror { get; init; } = true;

    /// <summary>宿主预先创建的摄像头采集器（录制前预览复用）；null 时按需创建。</summary>
    public CameraCapture? CameraSource { get; init; }

    /// <summary>录制前预览选定的屏幕矩形（物理像素）；null 用默认右下角。</summary>
    public PixelRect? CameraScreenRect { get; init; }

    /// <summary>
    /// 按分辨率给一个保守的 H.264 码率：1080p30 ≈ 7.5Mbps。
    /// 上限 40Mbps 是为了 4K 屏幕不至于糊，下限 2Mbps 是保证小区域也能看清文字。
    /// </summary>
    public int ResolveBitrate()
    {
        if (Bitrate > 0)
            return Bitrate;

        var pixels = (long)Region.Width * Region.Height;
        var estimated = (int)(pixels * FramesPerSecond * 0.12);
        return Math.Clamp(estimated, 2_000_000, 40_000_000);
    }
}
