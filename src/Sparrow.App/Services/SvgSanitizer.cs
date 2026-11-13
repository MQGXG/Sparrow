using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Sparrow.App.Services;

/// <summary>SVG 清洗结果。</summary>
public sealed record SvgSanitizeResult(bool Success, string? Svg, string? Error)
{
    public static SvgSanitizeResult Ok(string svg) => new(true, svg, null);

    public static SvgSanitizeResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 把模型或插件产出的 SVG 当成不可信输入处理：
/// 去脚本、去外链、去事件属性、限制大小，再交给渲染器。
/// </summary>
public static class SvgSanitizer
{
    public const int MaxBytes = 1_000_000;
    public const int MaxElements = 5_000;

    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "foreignObject", "image", "iframe", "video", "audio", "canvas"
    };

    public static SvgSanitizeResult Sanitize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return SvgSanitizeResult.Fail("SVG 为空");

        if (Encoding.UTF8.GetByteCount(source) > MaxBytes)
            return SvgSanitizeResult.Fail("SVG 超过 1 MB，已拒绝预览");

        XDocument document;

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
                MaxCharactersFromEntities = 1024
            };

            using var stringReader = new StringReader(source);
            using var reader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            return SvgSanitizeResult.Fail($"SVG 解析失败：{ex.Message}");
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
            return SvgSanitizeResult.Fail("根元素不是 <svg>");

        var elements = root.DescendantsAndSelf().ToList();
        if (elements.Count > MaxElements)
            return SvgSanitizeResult.Fail($"SVG 元素过多（{elements.Count}），已拒绝预览");

        foreach (var element in root.DescendantsAndSelf().ToList())
        {
            if (ForbiddenElements.Contains(element.Name.LocalName))
            {
                element.Remove();
                continue;
            }

            SanitizeAttributes(element);
        }

        return SvgSanitizeResult.Ok(document.ToString(SaveOptions.DisableFormatting));
    }

    private static void SanitizeAttributes(XElement element)
    {
        foreach (var attribute in element.Attributes().ToList())
        {
            var name = attribute.Name.LocalName;

            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                attribute.Remove();
                continue;
            }

            if (name is "href" or "src"
                || string.Equals(attribute.Name.NamespaceName, "http://www.w3.org/1999/xlink", StringComparison.Ordinal)
                   && name == "href")
            {
                if (IsExternal(attribute.Value))
                    attribute.Remove();
                continue;
            }

            if (name == "style" && IsUnsafeStyle(attribute.Value))
                attribute.Remove();
        }
    }

    private static bool IsExternal(string value)
    {
        var trimmed = value.TrimStart();

        return trimmed.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsafeStyle(string style) =>
        style.Contains("@import", StringComparison.OrdinalIgnoreCase)
        || style.Contains("url(http", StringComparison.OrdinalIgnoreCase)
        || style.Contains("url(https", StringComparison.OrdinalIgnoreCase)
        || style.Contains("url(//", StringComparison.OrdinalIgnoreCase)
        || style.Contains("javascript:", StringComparison.OrdinalIgnoreCase);
}
