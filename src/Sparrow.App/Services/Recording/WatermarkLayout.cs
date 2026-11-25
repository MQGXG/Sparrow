namespace Sparrow.App.Services.Recording;

/// <summary>
/// 水印布局：一个图文组合块，位置用相对坐标保存，
/// 这样 4K / 1080p / 720p 下位置和比例一致。
/// </summary>
public sealed record WatermarkLayout
{
    /// <summary>水印文字，可空。</summary>
    public string Text { get; init; } = "";

    /// <summary>水印图片本地路径（PNG/JPEG/WebP），可空。</summary>
    public string ImagePath { get; init; } = "";

    /// <summary>水印块中心的相对 X（0..1）。</summary>
    public double CenterX { get; init; } = 0.86;

    /// <summary>水印块中心的相对 Y（0..1）。</summary>
    public double CenterY { get; init; } = 0.92;

    /// <summary>水印块高度相对视频高度的比例（0.02..0.25）。</summary>
    public double Scale { get; init; } = 0.07;

    /// <summary>文字相对水印块高度的额外比例（0.5..2）。</summary>
    public double TextScale { get; init; } = 1.0;

    /// <summary>整体透明度（0.1..1）。</summary>
    public double Opacity { get; init; } = 0.85;

    /// <summary>没有文字也没有图片时不绘制。</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(ImagePath);

    /// <summary>默认右下角品牌水印。</summary>
    public static WatermarkLayout Default { get; } = new();
}
