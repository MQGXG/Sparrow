using System.Reflection;
using System.Runtime.Loader;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Plugins;

/// <summary>
/// 每个插件一个可回收的加载上下文。
///
/// 铁律 1（最重要）：SDK 必须由**默认** ALC 解析。
/// 如果从插件目录加载到另一份 SDK，宿主看到的 <c>ITool</c> 和插件实现的 <c>ITool</c>
/// 会是两个不同的类型，`tool is ITool` 返回 false，插件静默不注册。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryPath)
        : base(Path.GetFileNameWithoutExtension(entryPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;

        // 返回 null 表示交给默认 ALC —— 这是全流程里最关键的一行。
        //
        // P3 起的规则：SDK、UI 契约、Avalonia 都必须由默认 ALC 解析。
        // 插件若自带一份 Avalonia，Control 会变成两个类型，宿主强转直接失败；
        // 更隐蔽的是主题资源取不到、卸载回收不掉。
        if (name is not null
            && (name.StartsWith(SdkInfo.SdkAssemblyPrefix, StringComparison.Ordinal)
                || name.StartsWith(SdkInfo.UiContractAssemblyPrefix, StringComparison.Ordinal)
                || name.StartsWith("Avalonia", StringComparison.Ordinal)))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
