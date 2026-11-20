using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 结构化结果里的一段（多图识别时每张图一段）。
/// 单张时宿主不渲染这个列表，直接用纯文本结果。
/// </summary>
public sealed class TextSectionViewModel
{
    public TextSectionViewModel(TextSection section)
    {
        Title = section.Title;
        Text = section.Text;
        Status = section.Status;
        Lines = section.Lines;
        Preview = BuildPreview(section.Text);
    }

    public string Title { get; }

    public string Text { get; }

    public TextSectionStatus Status { get; }

    /// <summary>这段文字在源图片里的行框；有的话宿主可以叠识别框。</summary>
    public IReadOnlyList<OcrTextLine>? Lines { get; }

    public bool IsFailed => Status == TextSectionStatus.Failed;

    /// <summary>列表里显示的一段摘要，避免一次铺开几千字。</summary>
    public string Preview { get; }

    /// <summary>失败项没有正文，但要有可读的说明。</summary>
    public string Body => IsFailed ? "识别失败（图片无法解码、没有文字或 worker 异常）" : Text;

    private static string BuildPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // 换行压成空格：列表里一行摘要比多行更容易扫
        var flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 80 ? flattened : string.Concat(flattened.AsSpan(0, 80), "…");
    }
}
