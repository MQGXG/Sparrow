using Avalonia;

namespace Sparrow.App.Views;

/// <summary>
/// 可以被「标注模式」接管的窗口：告诉标注窗口图片当前显示在屏幕上的哪块区域，
/// 标注层就能贴着这块区域展开，而不是铺满整个屏幕。
/// </summary>
internal interface IAnnotationImageHost
{
    /// <summary>图片在屏幕上占据的物理像素矩形。</summary>
    PixelRect AnnotationImageRect { get; }
}
