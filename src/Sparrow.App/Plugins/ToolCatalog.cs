using Sparrow.App.Services;
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

    private PluginLoader? _loader;
    private ToolRegistry? _registry;
    private IReadOnlyList<PluginCandidate> _candidates = [];

    public ToolCatalog(string pluginsRoot, IAppLog log)
    {
        _pluginsRoot = pluginsRoot;
        _log = log;
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

    /// <summary>取工具注册表。首次调用触发扫描 + 加载，之后走缓存。</summary>
    public ToolRegistry GetRegistry()
    {
        EnsureBuilt();
        return _registry!;
    }

    private void EnsureBuilt()
    {
        if (_registry is not null)
            return;

        lock (_gate)
        {
            if (_registry is not null)
                return;

            var scanner = new PluginScanner(_pluginsRoot, _log);
            _candidates = scanner.Scan();

            _loader = new PluginLoader(_log);
            var tools = new List<ITool>();

            foreach (var candidate in _candidates)
            {
                var plugin = _loader.Load(candidate);
                if (plugin is not null)
                    tools.AddRange(plugin.Tools);
            }

            _registry = new ToolRegistry(tools);

            var valid = _candidates.Count(c => c.IsValid);
            _log.Info($"工具注册表构建完成：{tools.Count} 个工具 / {valid} 个有效插件 / {_candidates.Count} 个目录");
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
        }
    }
}
