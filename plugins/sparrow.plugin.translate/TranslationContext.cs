namespace Sparrow.Plugin.Translate;

/// <summary>
/// 长文分块翻译的「块间上下文」。
///
/// 分块是按长度切的，同一篇文章会被拆成几段分别送模型；模型看不到前一段，
/// 于是同一个术语/人名在两段里可能被翻成两种。把上一块末尾的原文和译文
/// 带进下一块，能让称谓和术语接上——这是长文档翻译最容易翻车的地方。
///
/// 只带末尾一小段：够对齐上下文，又不至于让提示词比正文还长。
/// </summary>
internal static class TranslationContext
{
    private const int TailLength = 200;

    /// <summary>
    /// 生成上下文提示块；没有上一块译文时返回空串（第一块不需要）。
    /// 返回值以空行结尾，调用方直接拼在正文前面。
    /// </summary>
    public static string Build(string previousSource, string previousTranslation)
    {
        if (string.IsNullOrWhiteSpace(previousTranslation))
            return string.Empty;

        return $"""
            上文已翻译（仅供参考，不要重复翻译，保持术语和称谓一致）：
            原文：{Tail(previousSource)}
            译文：{Tail(previousTranslation)}

            """;
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();

        return trimmed.Length <= TailLength
            ? trimmed
            : "…" + trimmed[^TailLength..];
    }
}
