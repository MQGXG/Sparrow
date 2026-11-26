using Avalonia;
using Avalonia.Controls;

namespace Sparrow.App.Views;

/// <summary>
/// 给已生成的图片（固定到屏幕的截图、滚动长图）补标注。
///
/// 直接复用截图窗口：标注工具、颜色、粗细、撤销/重做、识别/翻译都是现成的，
/// 纯标注模式只隐藏选区、角标和「保存/固定/滚动/录制」这类截图动作，
/// 不另写一套精简工具条。
/// </summary>
internal static class ImageAnnotator
{
    /// <summary>打开标注窗口，等用户点「完成」返回合成后的 PNG；取消返回 null。</summary>
    public static async Task<byte[]?> AnnotateAsync(Window owner, byte[] png)
    {
        var window = new CaptureWindow
        {
            AnnotationOnly = true,
            AnnotationSource = png,
            FreezeOnOpen = false,
            CaptureShowCursor = false
        };

        // 宿主（贴图 / 滚动截图结果）告诉标注层图片在屏幕上的位置：
        // 标注窗口只包住图片和工具条，铺满全屏会影响用户同时操作其他程序。
        if (owner is IAnnotationImageHost host
            && host.AnnotationImageRect is { Width: > 0, Height: > 0 } rect)
        {
            var scale = owner.RenderScaling > 0 ? owner.RenderScaling : 1;
            var imageWidth = Math.Max(1, rect.Width / scale);

            window.AnnotationBounds = rect;
            window.Position = new PixelPoint(rect.X, rect.Y);
            window.Width = Math.Max(CaptureWindow.AnnotationMinContentWidth, imageWidth) + 16;
            window.Height = Math.Max(1, rect.Height / scale) + 72;

            // StartAnnotationMode 排好版后再显示，避免先在默认位置闪一下
            window.Opacity = 0;
        }

        // 被标注的窗口先透明掉（不改 IsVisible：重新 Show() 会让贴图窗口
        // 按 CenterScreen 重新居中），标注结束后再还原。
        var ownerOpacity = owner.Opacity;
        owner.Opacity = 0;

        try
        {
            await window.ShowDialog(owner);

            // 标注时拖动过小窗：贴图跟着落到标注结束的位置（和 snow-shot 一致）
            if (owner is PinnedImageWindow
                && window.AnnotationResult is not null
                && window.AnnotationImageScreenRect is { } imageRect)
            {
                owner.Position = imageRect.Position;
            }

            return window.AnnotationResult;
        }
        finally
        {
            owner.Opacity = ownerOpacity;
        }
    }
}
