using System.Text.Json;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Plugins;

/// <summary>
/// 发现插件：只读 plugins/ 目录和清单，**不加载任何程序集**。
///
/// 校验顺序与 docs/plugin-model.md 第六节一致：
///   目录 → 清单存在 → JSON 可解析 → id == 目录名 → tools 非空
///   → 入口 dll 存在 → sdkVersion 兼容
/// </summary>
public sealed class PluginScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _root;
    private readonly IAppLog _log;

    public PluginScanner(string root, IAppLog log)
    {
        _root = root;
        _log = log;
    }

    public IReadOnlyList<PluginCandidate> Scan()
    {
        if (!Directory.Exists(_root))
        {
            // 目录不存在不是错误：用户可能删光了所有插件，宿主必须照常启动
            _log.Warn($"插件目录不存在，按空集合处理：{_root}");
            return [];
        }

        var candidates = new List<PluginCandidate>();

        foreach (var directory in Directory.EnumerateDirectories(_root).OrderBy(Path.GetFileName, StringComparer.Ordinal))
            candidates.Add(Read(directory));

        return candidates;
    }

    private PluginCandidate Read(string directory)
    {
        var directoryName = Path.GetFileName(directory);
        var manifestPath = Path.Combine(directory, "plugin.json");

        if (!File.Exists(manifestPath))
            return Fail(directory, directoryName, null, "缺少 plugin.json");

        PluginManifest? manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return Fail(directory, directoryName, manifestPath, $"清单解析失败：{ex.Message}");
        }

        if (manifest is null)
            return Fail(directory, directoryName, manifestPath, "清单内容为空");

        // 目录名是发现插件的唯一依据，不做模糊匹配
        if (!string.Equals(manifest.Id, directoryName, StringComparison.Ordinal))
            return Fail(directory, directoryName, manifestPath,
                $"清单 id「{manifest.Id}」与目录名「{directoryName}」不一致");

        if (manifest.Tools is not { Length: > 0 })
            return Fail(directory, directoryName, manifestPath, "tools 为空");

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            return Fail(directory, directoryName, manifestPath, "entryAssembly 为空");

        var entryPath = Path.Combine(directory, manifest.EntryAssembly);
        if (!File.Exists(entryPath))
            return Fail(directory, directoryName, manifestPath,
                $"入口程序集不存在：{manifest.EntryAssembly}");

        if (!IsSdkCompatible(manifest.SdkVersion))
            return Fail(directory, directoryName, manifestPath,
                $"插件需要 SDK {manifest.SdkVersion}，宿主是 {SdkInfo.Version}");

        _log.Info($"发现插件 {manifest.Id} {manifest.Version}（{manifest.Tools.Length} 个工具）");

        return new PluginCandidate(directory, directoryName, manifestPath, manifest, null);
    }

    private PluginCandidate Fail(string directory, string directoryName, string? manifestPath, string error)
    {
        _log.Warn($"跳过插件 {directoryName}：{error}");
        return new PluginCandidate(directory, directoryName, manifestPath, null, error);
    }

    /// <summary>只比较主版本：1.x 兼容 1.y，2.0 视为不兼容。</summary>
    private static bool IsSdkCompatible(string sdkVersion)
    {
        var dot = sdkVersion.IndexOf('.');
        var majorText = dot >= 0 ? sdkVersion[..dot] : sdkVersion;

        return int.TryParse(majorText, out var major) && major == SdkInfo.MajorVersion;
    }
}
