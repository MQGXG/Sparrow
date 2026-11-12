using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Plugins;

/// <summary>
/// 已加载的插件：清单 + 工具实例 + 它自己的加载上下文。
///
/// 注意：M1 这里持有的是强引用（<see cref="Tools"/>）。
/// docs/plugin-model.md 铁律 4 要求「宿主只存 WeakReference」，
/// 那是为了卸载后能真正回收；卸载 UI 与回收断言在 M1 后半段做，
/// 现在先把加载跑通，并在卸载时显式断开引用。
/// </summary>
public sealed class LoadedPlugin
{
    public required PluginManifest Manifest { get; init; }

    public required string Directory { get; init; }

    public required IReadOnlyList<ITool> Tools { get; init; }

    // 不标 required：Context 类型是 internal，不能出现在 public 的 required 成员上
    internal PluginLoadContext Context { get; init; } = null!;
}
