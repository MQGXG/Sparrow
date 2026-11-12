namespace Sparrow.App.Services;

/// <summary>
/// 滚动截图的捕获循环。
///
/// 交互层只负责开始/停止，这里每 240ms 抓一帧原始像素交给
/// <see cref="ScrollStitcher"/>，直到取消或失去重叠。
/// </summary>
internal static class ScrollCaptureService
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(240);

    public static async Task<byte[]?> CaptureAsync(
        int x,
        int y,
        int width,
        int height,
        Action<int>? onProgress,
        CancellationToken cancellationToken)
    {
        var stitcher = new ScrollStitcher(width, height);

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = ScreenCapture.CaptureRaw(x, y, width, height);
            if (frame is null)
                break;

            var outcome = stitcher.Append(frame, out _);
            onProgress?.Invoke(stitcher.TotalRows);

            // 找不到重叠说明滚太快或页面跳变，保留已拼好的部分
            if (outcome == AppendOutcome.LostTracking)
                break;

            try
            {
                await Task.Delay(FrameInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return stitcher.BuildPng();
    }
}
