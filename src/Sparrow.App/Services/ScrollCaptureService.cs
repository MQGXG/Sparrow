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
        Action<byte[]>? onPreview,
        CancellationToken cancellationToken)
    {
        using var stitcher = new ScrollStitcher(width, height);

        // 缩略图节流：最多 4 帧/秒，避免每帧都缩放开销
        var lastPreviewAt = 0L;

        while (!cancellationToken.IsCancellationRequested)
        {
            // 滚动截图不画光标：每帧都画会在拼接结果里重复出现
            var frame = ScreenCapture.CaptureRaw(
                x, y, width, height, includeCursor: false);
            if (frame is null)
                break;

            var outcome = stitcher.Append(frame, out var addedRows);
            onProgress?.Invoke(stitcher.TotalRows);

            var now = Environment.TickCount64;
            if (addedRows > 0 && onPreview is not null && now - lastPreviewAt >= 250)
            {
                lastPreviewAt = now;

                var preview = stitcher.BuildPreviewPng(280, 420);
                if (preview is not null)
                    onPreview(preview);
            }

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
