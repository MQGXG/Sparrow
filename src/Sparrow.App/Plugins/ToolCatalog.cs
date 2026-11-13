using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Plugins;

/// <summary>
/// 插件目录 → 工具注册表。
///
/// **懒加载**：构造时不碰磁盘、不加载程序集。
/// 第一次 <see cref="GetRegistry"/> 时才扫描 + 加载，
/// 这样「冷启动路径不加载插件程序集」这条约束是结构性成立的，
/// 不依赖开发者记得别在启动时调它（见 docs/plugin-model.md 出口条件 9）。
/// </summary>
public sealed class ToolCatalog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _pluginsRoot;
    private readonly IAppLog _log;
    private readonly AppSettings _settings;
    private readonly IAiClient? _ai;

    private PluginLoader? _loader;
    private ToolRegistry? _registry;
    private IReadOnlyList<PluginCandidate>? _scannedCandidates;
    private IReadOnlyList<PluginCandidate> _candidates = [];

    public ToolCatalog(string pluginsRoot, IAppLog log, AppSettings settings, IAiClient? ai = null)
    {
        _pluginsRoot = pluginsRoot;
        _log = log;
        _settings = settings;
        _ai = ai;
    }

    /// <summary>是否已经完成扫描 + 加载。用于验证懒加载是结构性成立的。</summary>
    public bool IsBuilt => _registry is not null;

    /// <summary>最近一次扫描结果。调用会触发构建。</summary>
    public IReadOnlyList<PluginCandidate> Candidates
    {
        get
        {
            EnsureBuilt();
            return _candidates;
        }
    }

    /// <summary>
    /// 只扫描清单，不加载程序集。
    /// 设置页只需要「有哪些插件」，不应该为了显示列表去加载 500 MB 的 OCR 引擎。
    /// </summary>
    public IReadOnlyList<PluginCandidate> ScanCandidates()
    {
        if (_scannedCandidates is not null)
            return _scannedCandidates;

        var scanner = new PluginScanner(_pluginsRoot, _log);
        _scannedCandidates = scanner.Scan();
        return _scannedCandidates;
    }

    /// <summary>取工具注册表。首次调用触发扫描 + 加载，之后走缓存。</summary>
    public ToolRegistry GetRegistry()
    {
        EnsureBuilt();
        return _registry!;
    }

    /// <summary>
    /// 卸载一个插件并重建注册表。
    /// 返回指向其 ALC 的弱引用，调用方可以 GC 后断言 <c>IsAlive == false</c>
    /// （docs/plugin-model.md 铁律 4 的验收方式）。
    /// </summary>
    public WeakReference? UnloadPlugin(string pluginId)
    {
        lock (_gate)
        {
            if (_loader is null)
                return null;

            var weak = _loader.Unload(pluginId);
            if (weak is null)
                return null;

            // 重建注册表，断开对已卸载工具的强引用；否则 ALC 永远回收不了
            _registry = BuildRegistry();
            return weak;
        }
    }

    private void EnsureBuilt()
    {
        if (_registry is not null)
            return;

        lock (_gate)
        {
            if (_registry is not null)
                return;

            _candidates = ScanCandidates();

            var disabled = new HashSet<string>(_settings.DisabledPlugins, StringComparer.Ordinal);
            _loader = new PluginLoader(_log, _ai);

            foreach (var candidate in _candidates)
            {
                if (disabled.Contains(candidate.Id))
                {
                    // 禁用 = 不加载程序集、不占内存、不进搜索结果
                    _log.Info($"跳过已禁用的插件 {candidate.Id}");
                    continue;
                }

                _loader.Load(candidate);
            }

            _registry = BuildRegistry();

            var valid = _candidates.Count(c => c.IsValid);
            _log.Info($"工具注册表构建完成：{_registry.Count} 个工具 / {valid} 个有效插件 / {_candidates.Count} 个目录 / {disabled.Count} 个禁用");
        }
    }

    private ToolRegistry BuildRegistry()
    {
        if (_loader is null)
            return new ToolRegistry([]);

        var entries = _loader.LoadedPlugins
            .SelectMany(plugin => plugin.Tools.Select(tool => new ToolEntry
            {
                Tool = tool,
                Host = plugin.Host
            }))
            .ToArray();

        return new ToolRegistry(entries);
    }

    /// <summary>
    /// 清空缓存，下次 <see cref="GetRegistry"/> 会重新扫描 + 加载。
    /// 用于设置页的「重新扫描」：用户手动放入插件后不用重启应用。
    /// </summary>
    public void Reload()
    {
        lock (_gate)
        {
            _loader?.Dispose();
            _loader = null;
            _registry = null;
            _candidates = [];
            _scannedCandidates = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _loader?.Dispose();
            _loader = null;
            _registry = null;
            _candidates = [];
            _scannedCandidates = null;
        }
    }
}
