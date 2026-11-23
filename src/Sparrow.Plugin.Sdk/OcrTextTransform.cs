using System.Text;

namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 识别结果的文本整理选项（对应 snow-shot 的 Formatting / Punctuation）。
///
/// 这些都是纯文本变换，不依赖模型，也不改行框——
/// 只影响「复制出去的文本」，行框和译文落点仍按原始行/格。
/// </summary>
public static class OcrTextTransform
{
    public const string FormattingKey = "formatting";
    public const string PunctuationKey = "punctuation";

    public const string FormatNone = "none";
    public const string FormatKeep = "keep";
    public const string FormatRemove = "remove";
    public const string FormatSmart = "smart";

    public const string PunctuationNone = "none";
    public const string PunctuationHalf = "half";
    public const string PunctuationFull = "full";

    /// <summary>先整理换行，再统一标点；未知值按「不处理」。</summary>
    public static string Apply(string text, string? formatting, string? punctuation)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = formatting switch
        {
            FormatRemove => RemoveLineBreaks(text),
            FormatSmart => SmartTypeset(text),
            _ => text
        };

        return punctuation switch
        {
            PunctuationHalf => ToHalfWidth(result),
            PunctuationFull => ToFullWidth(result),
            _ => result
        };
    }

    /// <summary>去掉换行：中文直接相连，英文/数字之间补一个空格。</summary>
    public static string RemoveLineBreaks(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lines = SplitLines(text);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
                continue;

            if (builder.Length > 0 && NeedsSpace(builder[^1], line[0]))
                builder.Append(' ');

            builder.Append(line);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 智能排版：保留句末换行，把被硬折行的句子接回去。
    /// 表格行（含 Tab）不合并，避免把一行表格拼成一串。
    /// </summary>
    public static string SmartTypeset(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lines = SplitLines(text);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (builder.Length == 0)
            {
                builder.Append(line);
                continue;
            }

            var last = builder[^1];
            var isBreak = IsSentenceEnd(last) || line.Contains('\t') || builder[^1] == '\t';

            if (isBreak)
            {
                builder.Append('\n');
                builder.Append(line);
                continue;
            }

            if (NeedsSpace(last, line[0]))
                builder.Append(' ');

            builder.Append(line);
        }

        return builder.ToString();
    }

    /// <summary>全角标点 → 半角（中文文本里的引号、逗号、句号等）。</summary>
    public static string ToHalfWidth(string text) =>
        Replace(text, FullToHalf);

    /// <summary>半角标点 → 全角。</summary>
    public static string ToFullWidth(string text) =>
        Replace(text, HalfToFull);

    private static string Replace(string text, Func<char, char?> map)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
            builder.Append(map(ch) ?? ch);

        return builder.ToString();
    }

    private static char? FullToHalf(char ch) => ch switch
    {
        '，' => ',',
        '。' => '.',
        '！' => '!',
        '？' => '?',
        '：' => ':',
        '；' => ';',
        '（' => '(',
        '）' => ')',
        '【' => '[',
        '】' => ']',
        '“' or '”' => '"',
        '‘' or '’' => '\'',
        '、' => ',',
        _ => null
    };

    private static char? HalfToFull(char ch) => ch switch
    {
        ',' => '，',
        '.' => '。',
        '!' => '！',
        '?' => '？',
        ':' => '：',
        ';' => '；',
        '(' => '（',
        ')' => '）',
        '[' => '【',
        ']' => '】',
        _ => null
    };

    /// <summary>换行拆行，兼容 CRLF / CR / LF。</summary>
    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    /// <summary>句末标点：这些字符后面保留换行。</summary>
    private static bool IsSentenceEnd(char ch) =>
        ch is '。' or '！' or '？' or '；' or '!' or '?' or ';';

    /// <summary>两侧都是中文不补空格；中英/英英之间补一个。</summary>
    private static bool NeedsSpace(char left, char right) =>
        !(IsCjk(left) && IsCjk(right)) && !char.IsWhiteSpace(left) && !char.IsWhiteSpace(right);

    private static bool IsCjk(char ch) =>
        ch >= 0x3000 && ch <= 0x9FFF;
}
