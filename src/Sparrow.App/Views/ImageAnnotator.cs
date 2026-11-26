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

        await window.ShowDialog(owner);
        return window.AnnotationResult;
    }
}
