namespace Sparrow.App.Services;

/// <summary>
/// 滚动截图的捕获循环。
///
/// 两种模式共用同一个拼接器：
/// - 手动：用户自己滚轮，按固定节奏抓帧；
/// - 自动：循环「发滚轮 → 等画面稳定 → 抓帧」，连续几轮没有新内容说明已经到底。
///
/// 交互层只负责开关和方向，具体节奏由 <see cref="ScrollCaptureControl"/> 实时控制。
/// </summary>
internal static class ScrollCaptureService
{
    /// <summary>手动模式抓帧间隔；自动模式的间隔由控制对象给。</summary>
    private static readonly TimeSpan ManualFrameInterval = TimeSpan.FromMilliseconds(240);

    /// <summary>自动模式连续多少轮没有新增内容就认为到底了。</summary>
    private const int IdleRoundsToStop = 3;

    public static async Task<byte[]?> CaptureAsync(
        int x,
        int y,
        int width,
        int height,
        ScrollCaptureControl control,
        Action<int>? onProgress,
        Action<byte[]>? onPreview,
        CancellationToken cancellationToken)
    {
        using var stitcher = new ScrollStitcher(width, height);

        // 缩略图节流：最多 4 帧/秒，避免每帧都缩放开销
        var lastPreviewAt = 0L;
        var idleRounds = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var auto = control.AutoScroll;

            if (auto)
            {
                WheelScroller.Scroll(
                    control.Horizontal,
                    WheelScroller.LinesForFrame(height));

                try
                {
                    await Task.Delay(control.IntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

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

            if (auto)
            {
                // 连续几轮都没新增：到底了，或者这一页根本不支持滚轮
                idleRounds = outcome == AppendOutcome.Duplicate ? idleRounds + 1 : 0;
                if (idleRounds >= IdleRoundsToStop)
                    break;

                continue;
            }

            try
            {
                await Task.Delay(ManualFrameInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return stitcher.BuildPng();
    }
}
