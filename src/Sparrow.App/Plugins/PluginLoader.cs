using System.Reflection;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Plugins;

/// <summary>
/// 把候选插件变成可执行的工具实例。
///
/// 只做加载，不做发现、不做注册——发现是 <see cref="PluginScanner"/> 的事，
/// 注册是 <see cref="ToolCatalog"/> 的事，这样每一步都能单独测。
/// </summary>
public sealed class PluginLoader : IDisposable
{
    private readonly IAppLog _log;
    private readonly List<LoadedPlugin> _loaded = [];
    private bool _disposed;

    public PluginLoader(IAppLog log) => _log = log;

    /// <summary>当前已加载的插件。注册表重建与卸载都基于它。</summary>
    public IReadOnlyList<LoadedPlugin> LoadedPlugins => _loaded;

    /// <summary>
    /// 卸载一个插件。返回指向其加载上下文的弱引用，
    /// 调用方可以据此断言「GC 后 ALC 真的被回收了」（铁律 4）。
    /// </summary>
    public WeakReference? Unload(string pluginId)
    {
        var plugin = _loaded.FirstOrDefault(p => p.Manifest.Id == pluginId);
        if (plugin is null)
            return null;

        // 先从列表移除，断开宿主对插件对象的强引用
        _loaded.Remove(plugin);

        var weak = new WeakReference(plugin.Context);
        plugin.Context.Unload();

        _log.Info($"已卸载插件 {pluginId}");
        return weak;
    }

    /// <summary>加载一个插件。失败返回 null 并记日志，绝不抛给调用方。</summary>
    public LoadedPlugin? Load(PluginCandidate candidate)
    {
        if (!candidate.IsValid || candidate.Manifest is null)
            return null;

        var manifest = candidate.Manifest;
        PluginLoadContext? context = null;

        try
        {
            context = new PluginLoadContext(candidate.EntryPath);
            var assembly = context.LoadFromAssemblyPath(candidate.EntryPath);
            var tools = FindTools(assembly, manifest);

            if (tools.Count == 0)
            {
                context.Unload();
                return null;
            }

            var plugin = new LoadedPlugin
            {
                Manifest = manifest,
                Directory = candidate.Directory,
                Tools = tools,
                Host = new ToolHost(manifest.Id, candidate.Directory, _log),
                Context = context
            };

            _loaded.Add(plugin);
            _log.Info($"已加载插件 {manifest.Id}，注册 {tools.Count} 个工具");
            return plugin;
        }
        catch (Exception ex)
        {
            _log.Error($"加载插件 {manifest.Id} 失败", ex);
            context?.Unload();
            return null;
        }
    }

    /// <summary>
    /// 在程序集里找 <see cref="ITool"/> 实现，并且只认清单 tools 里声明过的 ID。
    /// 「清单声明」这一层校验很重要：它让插件不能偷偷多注册一个工具。
    /// </summary>
    private IReadOnlyList<ITool> FindTools(Assembly assembly, PluginManifest manifest)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 部分类型加载失败时仍尝试其余类型，避免一个坏类型带走整个插件
            _log.Warn($"插件 {manifest.Id} 有类型加载失败，已跳过：{ex.LoaderExceptions.FirstOrDefault()?.Message}");
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        var declared = new HashSet<string>(manifest.Tools, StringComparer.Ordinal);
        var tools = new List<ITool>();

        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract || !typeof(ITool).IsAssignableFrom(type))
                continue;

            ITool? tool;
            try
            {
                tool = Activator.CreateInstance(type) as ITool;
            }
            catch (Exception ex)
            {
                _log.Error($"插件 {manifest.Id} 无法实例化 {type.FullName}", ex);
                continue;
            }

            if (tool is null)
                continue;

            var id = tool.Descriptor.Id;
            if (!declared.Contains(id))
            {
                _log.Warn($"插件 {manifest.Id} 的工具「{id}」未在清单 tools 中声明，已忽略");
                continue;
            }

            tools.Add(tool);
        }

        if (tools.Count == 0)
        {
            // 这个错误十有八九是铁律 1 被违反了：SDK 被拷进插件目录，ITool 变成两个类型
            _log.Error($"插件 {manifest.Id} 未找到可用的 ITool 实现。"
                + "常见原因：插件目录里出现了 Sparrow.Plugin.Sdk.dll，导致 ITool 类型不一致。");
        }

        return tools;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 真正卸载每个 ALC，而不是只清空列表——
        // 只清列表会让加载上下文一直存活到进程退出
        foreach (var plugin in _loaded)
            plugin.Context.Unload();

        _loaded.Clear();
        _log.Info("插件加载器已释放");
    }
}
