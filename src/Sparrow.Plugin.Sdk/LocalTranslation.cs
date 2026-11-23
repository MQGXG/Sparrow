namespace Sparrow.Plugin.Sdk;

/// <summary>术语表条目：原文 → 指定译法。</summary>
public sealed record LocalTranslationTerm(string Source, string Target);

/// <summary>
/// 一次本地翻译请求。
///
/// 只描述「翻什么、翻成什么」，不含提示词和模型参数——
/// 那些由宿主的翻译引擎按模型卡片决定，插件改不了。
/// </summary>
public sealed record LocalTranslationRequest
{
    /// <summary>用哪个本地模型（必须是 <see cref="ILocalModels.All"/> 里的 Id）。</summary>
    public required string ModelId { get; init; }

    public required string Text { get; init; }

    /// <summary>目标语言显示名，如 中文 / English / 日本語。</summary>
    public required string TargetLanguage { get; init; }

    /// <summary>语气：自然 / 正式 / 口语。</summary>
    public string Style { get; init; } = "自然";

    /// <summary>保留原文换行、列表与代码块。</summary>
    public bool KeepFormatting { get; init; } = true;

    /// <summary>术语表；宿主负责按「必须这么翻」的方式写进提示词。</summary>
    public IReadOnlyList<LocalTranslationTerm> Terms { get; init; } = [];
}

/// <summary>
/// 本地翻译引擎（宿主能力）。
///
/// 为什么放在宿主：模型加载、native 库、提示词模板、采样参数、分块与清理
/// 都是「一个引擎」的事，不该由每个用到翻译的插件各抄一份。
/// 翻译工具和 OCR 的「翻译为」共用这一份实现。
/// </summary>
public interface ILocalTranslation
{
    /// <summary>指定模型是否已下载。未下载时宿主不会自动下载。</summary>
    bool IsAvailable(string modelId);

    /// <summary>
    /// 翻译整段文本（内部按块处理，顺序拼接）。
    /// 模型不可用或推理失败返回 null，插件负责给出可读提示。
    /// </summary>
    Task<string?> TranslateAsync(
        LocalTranslationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式翻译：每次 yield 的是「截至目前的完整译文」，不是增量片段。
    /// 默认实现退化为一次 <see cref="TranslateAsync"/>。
    /// </summary>
    async IAsyncEnumerable<string> StreamAsync(
        LocalTranslationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = await TranslateAsync(request, cancellationToken);
        if (!string.IsNullOrEmpty(text))
            yield return text;
    }
}

/// <summary>没有本地翻译能力时的空实现（测试或精简宿主）。</summary>
public sealed class NullLocalTranslation : ILocalTranslation
{
    public static NullLocalTranslation Instance { get; } = new();

    public bool IsAvailable(string modelId) => false;

    public Task<string?> TranslateAsync(
        LocalTranslationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
