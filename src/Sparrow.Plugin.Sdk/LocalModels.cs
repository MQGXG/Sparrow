namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 一个可下载的本地模型（GGUF）。
///
/// 清单由宿主维护：插件只按 Id 请求，不关心从哪下载、存哪。
/// </summary>
public sealed record LocalModelInfo(
    string Id,
    string Name,
    string Description,
    long SizeBytes,
    int MemoryGb,
    string License,
    IReadOnlyList<string> DownloadUrls);

/// <summary>
/// 本地模型管理。
///
/// 宿主负责下载、校验、缓存和进度展示；插件只拿「模型文件在哪」。
/// 不放在插件里的原因：下载要写用户目录、要显示进度，这些都是宿主的事；
/// 而且同一个模型可能被多个插件用到，缓存必须有统一入口。
/// </summary>
public interface ILocalModels
{
    /// <summary>宿主认识的模型清单（含大小、内存要求和许可）。</summary>
    IReadOnlyList<LocalModelInfo> All { get; }

    /// <summary>模型是否已经下载并校验通过。</summary>
    bool IsInstalled(string modelId);

    /// <summary>
    /// 确保模型可用，返回本地文件路径。
    ///
    /// 未安装时下载（进度 0..1）；下载源按 <see cref="LocalModelInfo.DownloadUrls"/>
    /// 顺序尝试，第一个成功即止。失败返回 null，插件应给出可读提示。
    /// </summary>
    Task<string?> EnsureAsync(
        string modelId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除已下载的模型，释放磁盘。</summary>
    Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default);
}

/// <summary>没有本地模型能力时的空实现：宿主未注入时插件拿到它。</summary>
public sealed class NullLocalModels : ILocalModels
{
    public static NullLocalModels Instance { get; } = new();

    public IReadOnlyList<LocalModelInfo> All => [];

    public bool IsInstalled(string modelId) => false;

    public Task<string?> EnsureAsync(
        string modelId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
