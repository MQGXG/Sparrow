using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sparrow.App.Services;

/// <summary>
/// 剪贴板内容的轻量分类器：入库时打自动标签，用来筛选，不改变条目类型。
///
/// 规则刻意保守——只给「整段内容就是这类东西」下结论，
/// 不做代码/自然语言猜测，避免把普通句子误标成代码。
/// 手工标签仍然由用户决定。
/// </summary>
public static partial class ClipboardClassifier
{
    public const string TagLink = "链接";
    public const string TagEmail = "邮箱";
    public const string TagColor = "颜色";
    public const string TagJson = "JSON";
    public const string TagPath = "路径";
    public const string TagImage = "图片";
    public const string TagFile = "文件";

    /// <summary>文本内容的自动标签；可能为空。</summary>
    public static IReadOnlyList<string> ClassifyText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return [];

        var tags = new List<string>(2);

        if (IsLink(trimmed))
            tags.Add(TagLink);

        if (EmailRegex().IsMatch(trimmed))
            tags.Add(TagEmail);

        if (ColorRegex().IsMatch(trimmed))
            tags.Add(TagColor);

        if (IsJson(trimmed))
            tags.Add(TagJson);

        if (PathRegex().IsMatch(trimmed))
            tags.Add(TagPath);

        return tags;
    }

    /// <summary>图片条目的自动标签：固定为「图片」。</summary>
    public static IReadOnlyList<string> ClassifyImage() => [TagImage];

    /// <summary>文件条目：全是图片算「图片」，否则算「文件」。</summary>
    public static IReadOnlyList<string> ClassifyFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return [];

        var allImages = paths.All(path => IsImageExtension(Path.GetExtension(path)));
        return [allImages ? TagImage : TagFile];
    }

    private static bool IsLink(string text)
    {
        if (!LinkRegex().IsMatch(text))
            return false;

        // 「http://... 后面跟一段话」不算链接，避免误标；
        // 只接受单个地址（中间不能有空白）
        return !text.Any(char.IsWhiteSpace);
    }

    private static bool IsJson(string text)
    {
        if (text[0] is not ('{' or '['))
            return false;

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsImageExtension(string extension) =>
        extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff";

    [GeneratedRegex(@"^(https?|ftp)://\S+$|^www\.\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^(#[0-9a-fA-F]{3,8}|rgba?\([^)]*\))$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex(@"^([A-Za-z]:\\|\\\\)[^\r\n]*$")]
    private static partial Regex PathRegex();
}
