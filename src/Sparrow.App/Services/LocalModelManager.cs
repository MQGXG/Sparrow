using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 本地模型（GGUF）的下载与缓存。
///
/// 文件落在 <c>%LocalAppData%\Sparrow\models\{id}\model.gguf</c>。
/// 下载源按 <see cref="LocalModelInfo.DownloadUrls"/> 顺序尝试，第一个成功即止；
/// 将来要自托管，只要在列表前面插一条自己的镜像地址，其余代码不用动。
///
/// 校验只做「文件非空」：清单里的大小是估算值（量化版本略有差异），
/// 卡死大小反而会让可用的模型被判失败。SHA256 等以后有稳定源再加。
/// </summary>
public sealed class LocalModelManager : ILocalModels
{
    private const string ModelFileName = "model.gguf";

    private readonly IAppLog _log;
    private readonly HttpClient _http;
    private readonly string _root;

    public LocalModelManager(IAppLog log, string? root = null, HttpClient? http = null)
    {
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromHours(2) };
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "models");
    }

    /// <summary>
    /// 当前支持的本地模型。
    ///
    /// 下载源按顺序尝试：ModelScope 在前（国内直连），HuggingFace 兜底（官方仓库）。
    /// 将来要自托管，在数组最前面插一条自己的镜像地址即可，别处不用动。
    /// </summary>
    public IReadOnlyList<LocalModelInfo> All { get; } =
    [
        new(
            "hy-mt-1.8b",
            "混元翻译 1.5 · 1.8B",
            "33 种语言互译，小巧快速，适合大多数电脑",
            1_133_080_512,
            4,
            "腾讯混元社区协议（不适用于欧盟、英国和韩国）",
            [
                "https://modelscope.cn/models/Tencent-Hunyuan/HY-MT1.5-1.8B-GGUF/resolve/master/HY-MT1.5-1.8B-Q4_K_M.gguf",
                "https://huggingface.co/tencent/HY-MT1.5-1.8B-GGUF/resolve/main/HY-MT1.5-1.8B-Q4_K_M.gguf"
            ]),
        new(
            "hy-mt-7b",
            "混元翻译 1.5 · 7B",
            "WMT25 冠军模型升级版，译文更地道，需要较高配置",
            4_624_649_312,
            16,
            "腾讯混元社区协议（不适用于欧盟、英国和韩国）",
            [
                "https://modelscope.cn/models/Tencent-Hunyuan/HY-MT1.5-7B-GGUF/resolve/master/HY-MT1.5-7B-Q4_K_M.gguf",
                "https://huggingface.co/tencent/HY-MT1.5-7B-GGUF/resolve/main/HY-MT1.5-7B-Q4_K_M.gguf"
            ]),
        new(
            "translategemma-4b",
            "TranslateGemma · 4B",
            "55 种语言，小语种覆盖更广",
            2_300_000_000,
            8,
            "Gemma 使用条款",
            // 官方未提供稳定的 GGUF 直链；留空时设置页会提示「暂未配置下载源」，
            // 用户仍可在设置里填自己的镜像地址
            [])
    ];

    public bool IsInstalled(string modelId) => File.Exists(PathFor(modelId));

    /// <summary>
    /// 自托管镜像根地址（可选）。填了就优先从 <c>{镜像}/{原文件名}</c> 下载。
    /// 由设置页写入，运行期可改，不需要重建管理器。
    /// </summary>
    public string? MirrorUrl { get; set; }

    /// <summary>
    /// 某模型实际会尝试的下载地址：镜像（如果有）在前，内置源随后。
    /// 镜像地址只换前缀、不换文件名——自托管只要把同名文件放上去就能用。
    /// </summary>
    public IReadOnlyList<string> SourcesFor(string modelId)
    {
        var model = All.FirstOrDefault(item => item.Id == modelId);
        if (model is null || model.DownloadUrls.Count == 0)
            return model?.DownloadUrls ?? [];

        var mirror = MirrorUrl?.Trim();
        if (string.IsNullOrEmpty(mirror))
            return model.DownloadUrls;

        var fileName = Path.GetFileName(new Uri(model.DownloadUrls[0]).AbsolutePath);
        return [mirror.TrimEnd('/') + "/" + fileName, .. model.DownloadUrls];
    }

    /// <summary>模型文件路径；没下载时也返回预期路径，调用方用 IsInstalled 判断。</summary>
    public string PathFor(string modelId) =>
        Path.Combine(_root, modelId, ModelFileName);

    public async Task<string?> EnsureAsync(
        string modelId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = All.FirstOrDefault(item => item.Id == modelId);
        if (model is null)
        {
            _log.Warn($"未知的本地模型：{modelId}");
            return null;
        }

        var path = PathFor(modelId);
        if (File.Exists(path))
            return path;

        if (model.DownloadUrls.Count == 0)
        {
            _log.Warn($"模型「{model.Name}」没有配置下载源，请在设置里填一个镜像地址");
            return null;
        }

        var sources = SourcesFor(modelId);
        if (sources.Count == 0)
        {
            _log.Warn($"模型「{model.Name}」没有可用的下载地址");
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        foreach (var url in sources)
        {
            try
            {
                if (await DownloadAsync(url, path, progress, cancellationToken))
                {
                    _log.Info($"本地模型已就绪：{model.Name}");
                    return path;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"模型下载失败（{url}）：{ex.Message}");
            }
        }

        return null;
    }

    public Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(PathFor(modelId));
        if (directory is null || !Directory.Exists(directory))
            return Task.FromResult(false);

        try
        {
            Directory.Delete(directory, recursive: true);
            _log.Info($"已删除本地模型：{modelId}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _log.Warn($"删除模型失败（{modelId}）：{ex.Message}");
            return Task.FromResult(false);
        }
    }

    private async Task<bool> DownloadAsync(
        string url,
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var part = path + ".part";

        using var response = await _http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _log.Warn($"模型下载返回 HTTP {(int)response.StatusCode}：{url}");
            return false;
        }

        var total = response.Content.Headers.ContentLength ?? 0;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(part))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;

                if (total > 0)
                    progress?.Report((double)written / total);
            }
        }

        var info = new FileInfo(part);
        if (info.Length == 0)
        {
            File.Delete(part);
            _log.Warn($"模型下载为空文件：{url}");
            return false;
        }

        File.Move(part, path, overwrite: true);
        progress?.Report(1);
        return true;
    }
}
