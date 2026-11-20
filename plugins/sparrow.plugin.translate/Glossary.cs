using System.Text;

namespace Sparrow.Plugin.Translate;

/// <summary>
/// 术语表：用户指定「这个词必须这么翻」。
///
/// 参数是一个字符串（多行输入），每行 <c>原文=译文</c>；
/// 也接受 <c>＝</c>、<c>→</c> 和分号分隔，避免用户为了对齐格式来回折腾。
///
/// 术语表比「带上文」更可靠：它不依赖模型去理解上下文，而是直接给出答案。
/// </summary>
internal static class Glossary
{
    /// <summary>最多带这么多条：再多就是在污染提示词，而不是帮助翻译。</summary>
    private const int MaxEntries = 30;

    /// <summary>原始输入的长度上限，防止一次粘贴上千行。</summary>
    private const int MaxInputLength = 2000;

    private static readonly char[] Separators = ['\n', '\r', ';', '；'];

    private static readonly char[] Delimiters = ['=', '＝', '→'];

    /// <summary>解析成词条；空行、没有分隔符、重复原文都会被丢弃。</summary>
    public static IReadOnlyList<(string Source, string Target)> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var text = raw.Length > MaxInputLength ? raw[..MaxInputLength] : raw;
        var entries = new List<(string Source, string Target)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (entries.Count >= MaxEntries)
                break;

            var item = line.Trim().TrimStart('-', '•', '*').Trim();
            var index = item.IndexOfAny(Delimiters);

            if (index <= 0 || index >= item.Length - 1)
                continue;

            var source = item[..index].Trim();
            var target = item[(index + 1)..].Trim();

            if (source.Length == 0 || target.Length == 0 || !seen.Add(source))
                continue;

            entries.Add((source, target));
        }

        return entries;
    }

    /// <summary>渲染成提示词块；没有词条时返回空串（调用方直接拼接）。</summary>
    public static string ToPrompt(IReadOnlyList<(string Source, string Target)> entries)
    {
        if (entries.Count == 0)
            return string.Empty;

        var builder = new StringBuilder("术语表（这些词必须按指定译法翻译）：\n");

        foreach (var (source, target) in entries)
            builder.Append(source).Append(" → ").Append(target).Append('\n');

        return builder.Append('\n').ToString();
    }
}
