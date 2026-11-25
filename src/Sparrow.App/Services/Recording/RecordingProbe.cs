using Avalonia;
using Sparrow.App.Services;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 无 UI 的录制自检入口：设置环境变量后启动应用，录一段固定时长再退出。
/// 只用于开发期验证「帧源 → 编码 → 落盘」，不参与正常交互。
///
/// 用法：
///   $env:SPARROW_RECORD_PROBE="5"; dotnet run --project src/Sparrow.App
///   $env:SPARROW_RECORD_PROBE="5:1280x720"      # 指定左上角区域
///
/// 输出到 %TEMP%\sparrow-record-probe\probe-*.mp4。
/// </summary>
internal static class RecordingProbe
{
    public const string EnvironmentVariable = "SPARROW_RECORD_PROBE";

    /// <summary>环境变量存在时启动一次录制。返回 null 表示不是自检模式。</summary>
    public static RecordingSession? TryStart(out int seconds, out string? error)
    {
        seconds = 0;
        error = null;

        var raw = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split(':', 2);

        if (!int.TryParse(parts[0], out seconds) || seconds <= 0)
            seconds = 5;

        var region = parts.Length > 1
            ? ParseRegion(parts[1])
            : WgcCapture.GetPrimaryMonitorBounds();

        var window = GetProbeWindow();

        if (window != IntPtr.Zero)
            region = default;

        var directory = Path.Combine(Path.GetTempPath(), "sparrow-record-probe");
        var gif = Environment.GetEnvironmentVariable("SPARROW_RECORD_GIF") == "1";
        var extension = gif ? ".gif" : ".mp4";
        var path = Path.Combine(directory, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");

        var options = new RecordingOptions
        {
            Region = region,
            WindowHandle = window,
            OutputPath = path,
            FramesPerSecond = 30,
            Format = gif ? "gif" : "mp4",
            GifFps = 15,
            GifWidth = 720,
            Watermark = BuildProbeWatermark(),
            MaxDuration = int.TryParse(
                Environment.GetEnvironmentVariable("SPARROW_RECORD_MAX"),
                out var maxSeconds) && maxSeconds > 0
                ? TimeSpan.FromSeconds(maxSeconds)
                : null,
            // probe 可选录音频：SPARROW_RECORD_AUDIO=1
            SystemAudio = Environment.GetEnvironmentVariable("SPARROW_RECORD_AUDIO") == "1"
        };

        return RecordingSession.TryStart(options, out error);
    }

    /// <summary>解析 "1280x720" 形式的尺寸，取屏幕左上角。</summary>
    private static PixelRect ParseRegion(string text)
    {
        var parts = text.Split('x');

        if (parts.Length == 2
            && int.TryParse(parts[0], out var width)
            && int.TryParse(parts[1], out var height)
            && width > 0
            && height > 0)
        {
            return new PixelRect(0, 0, width, height);
        }

        return WgcCapture.GetPrimaryMonitorBounds();
    }

    /// <summary>SPARROW_RECORD_WINDOW=foreground 时录当前前台窗口。</summary>
    private static IntPtr GetProbeWindow()
    {
        var value = Environment.GetEnvironmentVariable("SPARROW_RECORD_WINDOW");
        return string.Equals(value, "foreground", StringComparison.OrdinalIgnoreCase)
            ? GetForegroundWindow()
            : IntPtr.Zero;
    }

    /// <summary>probe 水印：SPARROW_RECORD_WATERMARK / SPARROW_RECORD_WATERMARK_IMAGE。</summary>
    private static WatermarkLayout? BuildProbeWatermark()
    {
        var text = Environment.GetEnvironmentVariable("SPARROW_RECORD_WATERMARK") ?? "";
        var image = Environment.GetEnvironmentVariable("SPARROW_RECORD_WATERMARK_IMAGE") ?? "";

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(image))
            return null;

        return new WatermarkLayout
        {
            Text = text,
            ImagePath = image,
            CenterX = 0.86,
            CenterY = 0.92,
            Scale = 0.07,
            Opacity = 0.85
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
