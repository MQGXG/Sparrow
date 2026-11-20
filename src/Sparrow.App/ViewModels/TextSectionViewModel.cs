using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 结构化结果里的一段（多图识别时每张图一段）。
/// 单张时宿主不渲染这个列表，直接用纯文本结果。
/// </summary>
public sealed partial class TextSectionViewModel : ObservableObject
{
    public TextSectionViewModel(TextSection section, InputImageViewModel? sourceImage = null)
    {
        Title = section.Title;
        Text = section.Text;
        Status = section.Status;
        SourceIndex = section.SourceIndex;
        Lines = section.Lines;
        Preview = BuildPreview(section.Text);

        // 原图从输入区拿，不复制字节；没有就退化成只有文字
        ImagePng = sourceImage?.PreviewBytes;
        ImageWidth = sourceImage?.Input.Width ?? 0;
        ImageHeight = sourceImage?.Input.Height ?? 0;
    }

    public string Title { get; }

    public string Text { get; }

    public TextSectionStatus Status { get; }

    /// <summary>这段文字在源图片里的行框；有的话可以叠识别框。</summary>
    public IReadOnlyList<OcrTextLine>? Lines { get; }

    public bool IsFailed => Status == TextSectionStatus.Failed;

    /// <summary>对应输入图片的下标，右侧「只看当前」靠它和图对上。删图后会被前移。</summary>
    public int SourceIndex { get; set; }

    /// <summary>列表里显示的一段摘要，避免一次铺开几千字。</summary>
    public string Preview { get; }

    /// <summary>失败项没有正文，但要有可读的说明。</summary>
    public string Body => IsFailed ? "识别失败（图片无法解码、没有文字或 worker 异常）" : Text;

    /// <summary>这张图的原图字节；没有就只显示文字。</summary>
    public byte[]? ImagePng { get; }

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public bool HasImage => ImagePng is { Length: > 0 } && ImageWidth > 0 && ImageHeight > 0;

    public bool HasLines => Lines is { Count: > 0 };

    /// <summary>
    /// 展开后显示原图 + 全文；折叠时只显示摘要。
    /// 默认展开：多图识别的核心用途就是「图对文」，默认折叠会逼用户逐张点开。
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>识别框开关：默认显示，可以关掉看干净的原图。</summary>
    [ObservableProperty]
    private bool _showBoxes = true;

    private static string BuildPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // 换行压成空格：列表里一行摘要比多行更容易扫
        var flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 80 ? flattened : string.Concat(flattened.AsSpan(0, 80), "…");
    }
}
