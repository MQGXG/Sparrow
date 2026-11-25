using Avalonia;
using Sparrow.App.Services.Recording;

namespace Sparrow.App.Overlays;

/// <summary>
/// 摄像头预览的唯一入口：录制前由 CreateRecordingSessionAsync 显示，
/// 会话创建后 Attach 到会话，停止/取消时由宿主 Close。
/// 预览窗口本身不拥有 CameraCapture，采集器归会话/调用方释放。
/// </summary>
internal static class CameraPreviewHost
{
    private static CameraPreviewWindow? _window;

    public static CameraPreviewWindow? Current => _window;

    public static void Show(
        CameraCapture camera,
        PixelRect region,
        PixelRect initialRect,
        bool mirror,
        Action<PixelRect> layoutChanged)
    {
        Close();

        _window = new CameraPreviewWindow(camera, region, initialRect, mirror, layoutChanged);
        _window.Show();
    }

    /// <summary>会话创建后把预览的布局目标切到会话。</summary>
    public static void Attach(RecordingSession session) => _window?.Attach(session);

    public static void Close()
    {
        _window?.Close();
        _window = null;
    }
}
