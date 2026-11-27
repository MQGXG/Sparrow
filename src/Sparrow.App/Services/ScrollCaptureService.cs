namespace Sparrow.App.Services;

/// <summary>
/// 滚动截图的捕获循环。
///
/// 按方向选用同一个接口下的拼接器：
/// - 纵向：<see cref="ScrollStitcher"/>，按行拼接，支持磁盘溢写；
/// - 横向：<see cref="HorizontalScrollStitcher"/>，按列拼接。
///
/// 两种模式共用同一套节奏：
/// - 手动：用户自己滚轮，按固定节奏抓帧；
/// - 自动：循环「抓帧 → 发滚轮 → 等画面稳定」，连续几轮没有新内容说明已经到底。
///
/// 交互层只负责开关和方向，具体节奏由 <see cref="ScrollCaptureControl"/> 实时控制。
/// </summary>
internal static class ScrollCaptureService
{
    /// <summary>手动模式抓帧间隔；自动模式的间隔由控制对象给。</summary>
    private static readonly TimeSpan ManualFrameInterval = TimeSpan.FromMilliseconds(240);

    /// <summary>自动模式连续多少轮没有新增内容就认为到底了。</summary>
    private const int IdleRoundsToStop = 3;

    /// <summary>
    /// 失配后连续重试多少次。平滑滚动、页面重绘会让相邻两帧对不上，
    /// 直接停止会把「滚动一点就断」变成常态。
    /// </summary>
    private const int LostRoundsToStop = 3;

    /// <summary>失配重试间隔：等滚动动画落定后再抓一帧。</summary>
    private static readonly TimeSpan LostRetryDelay = TimeSpan.FromMilliseconds(120);

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
        using var stitcher = control.Horizontal
            ? (IScrollStitcher)new HorizontalScrollStitcher(width, height)
            : new ScrollStitcher(width, height);

        // 缩略图节流：最多 4 帧/秒，避免每帧都缩放开销
        var lastPreviewAt = 0L;
        var idleRounds = 0;
        var lostRounds = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            // 先抓一帧再滚动：自动模式如果先滚，页面顶部那一屏会永远丢掉。
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

            if (outcome == AppendOutcome.LostTracking)
            {
                // 可能刚好抓到平滑滚动的过渡帧：稍等再抓，连续几次都对不上才放弃
                lostRounds++;
                if (lostRounds >= LostRoundsToStop)
                    break;

                try
                {
                    await Task.Delay(LostRetryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            lostRounds = 0;

            if (!control.AutoScroll)
            {
                try
                {
                    await Task.Delay(ManualFrameInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            // 连续几轮都没新增：到底了，或者这一页根本不支持滚轮
            idleRounds = outcome == AppendOutcome.Duplicate ? idleRounds + 1 : 0;
            if (idleRounds >= IdleRoundsToStop)
                break;

            WheelScroller.Scroll(
                control.Horizontal,
                WheelScroller.LinesForFrame(control.Horizontal ? width : height));

            try
            {
                await Task.Delay(control.IntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return stitcher.BuildPng();
    }
}
