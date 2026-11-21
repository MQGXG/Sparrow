namespace Sparrow.App.Settings;

/// <summary>
/// 一套保存下来的标注样式：颜色、线宽、马赛克模式、圆角。
/// 截图工具条的「模板」菜单用它做三槽快速切换。
/// </summary>
public sealed record AnnotationPreset(
    string Name,
    string Color,
    double Width,
    bool MosaicBlur,
    double CornerRadius);
