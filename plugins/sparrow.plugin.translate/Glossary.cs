using Sparrow.Plugin.Sdk;

namespace Sparrow.Plugin.Translate;

/// <summary>
/// 术语表：用户指定「这个词必须这么翻」。
///
/// 参数是一个字符串（多行输入），每行 <c>原文=译文</c>；
/// 也接受 <c>＝</c>、<c>→</c> 和分号分隔，避免用户为了对齐格式来回折腾。
///
/// 解析留在插件（这是工具自己的参数格式），渲染成提示词由宿主的翻译引擎负责。
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
    public static IReadOnlyList<LocalTranslationTerm> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var text = raw.Length > MaxInputLength ? raw[..MaxInputLength] : raw;
        var terms = new List<LocalTranslationTerm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (terms.Count >= MaxEntries)
                break;

            var item = line.Trim().TrimStart('-', '•', '*').Trim();
            var index = item.IndexOfAny(Delimiters);

            if (index <= 0 || index >= item.Length - 1)
                continue;

            var source = item[..index].Trim();
            var target = item[(index + 1)..].Trim();

            if (source.Length == 0 || target.Length == 0 || !seen.Add(source))
                continue;

            terms.Add(new LocalTranslationTerm(source, target));
        }

        return terms;
    }
}
