using System.Text;
using System.Text.RegularExpressions;

namespace Sparrow.App.Services;

/// <summary>复制前的文本变换。大小写类只作用于字母，中文等无大小写文本原样保留。</summary>
public enum PasteTransform
{
    PlainText,
    Upper,
    Lower,
    Capitalize,
    Sentence,
    Camel,
    Trim
}

/// <summary>
/// 纯函数变换，便于测试。规则对齐 Witch Clipboard 的 <c>apply_paste_transform</c>：
/// 大小写只处理字母；<see cref="PasteTransform.Capitalize"/> 会压缩空白；
/// <see cref="PasteTransform.Camel"/> 按非字母数字切词。
/// </summary>
public static partial class PasteTransforms
{
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex WordSeparator();

    public static string Apply(string text, PasteTransform transform) => transform switch
    {
        PasteTransform.Upper => text.ToUpperInvariant(),
        PasteTransform.Lower => text.ToLowerInvariant(),
        PasteTransform.Capitalize => Capitalize(text),
        PasteTransform.Sentence => Sentence(text),
        PasteTransform.Camel => Camel(text),
        PasteTransform.Trim => text.Trim(),
        _ => text
    };

    /// <summary>菜单上显示的名字。</summary>
    public static string Label(PasteTransform transform) => transform switch
    {
        PasteTransform.PlainText => "纯文本",
        PasteTransform.Upper => "全部大写",
        PasteTransform.Lower => "全部小写",
        PasteTransform.Capitalize => "每词首字母大写",
        PasteTransform.Sentence => "句首大写",
        PasteTransform.Camel => "驼峰命名",
        PasteTransform.Trim => "去掉首尾空白",
        _ => transform.ToString()
    };

    /// <summary>按空白切词，每词首字母大写，其余小写；连续空白压成一个空格。</summary>
    private static string Capitalize(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return string.Empty;

        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < words.Length; index++)
        {
            if (index > 0)
                builder.Append(' ');

            var word = words[index];
            builder.Append(char.ToUpperInvariant(word[0]));
            builder.Append(word[1..].ToLowerInvariant());
        }

        return builder.ToString();
    }

    /// <summary>只在句首把 ASCII 字母大写，其余小写；句号 / 感叹号 / 问号后重置。</summary>
    private static string Sentence(string text)
    {
        var builder = new StringBuilder(text.Length);
        var capitalizeNext = true;

        foreach (var character in text)
        {
            if (capitalizeNext && char.IsAsciiLetter(character))
            {
                builder.Append(char.ToUpperInvariant(character));
                capitalizeNext = false;
            }
            else
            {
                builder.Append(char.ToLowerInvariant(character));
            }

            if (character is '.' or '!' or '?')
                capitalizeNext = true;
        }

        return builder.ToString();
    }

    /// <summary>按非字母数字切词，首词全小写，后续词首字母大写。</summary>
    private static string Camel(string text)
    {
        var words = WordSeparator().Split(text);
        var builder = new StringBuilder(text.Length);
        var first = true;

        foreach (var raw in words)
        {
            if (raw.Length == 0)
                continue;

            var word = raw.ToLowerInvariant();

            if (first)
            {
                builder.Append(word);
                first = false;
                continue;
            }

            builder.Append(char.ToUpperInvariant(word[0]));
            builder.Append(word[1..]);
        }

        return builder.ToString();
    }
}
