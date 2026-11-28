namespace Sparrow.App.Plugins;

/// <summary>
/// plugin.json 的内存模型。
/// 字段与 docs/plugin-model.md 第 4.1 节一一对应，不多不少。
/// </summary>
public sealed record PluginManifest
{
    /// <summary>插件唯一 ID，必须等于目录名。</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    /// <summary>编译时依赖的 SDK 版本，形如 1.0。只比较主版本。</summary>
    public required string SdkVersion { get; init; }

    /// <summary>入口 dll 文件名，相对插件目录。</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>本插件提供的工具 ID 列表，至少一个。</summary>
    public required string[] Tools { get; init; }

    public string[] Capabilities { get; init; } = [];

    /// <summary>
    /// 插件建议把本插件声明的工具默认固定进托盘。
    /// 面向「没有页面、只有一次动作」的工具（如窗口置顶）；用户仍可在托盘里移除。
    /// </summary>
    public bool TrayDefault { get; init; }

    public string? Author { get; init; }

    public string? Description { get; init; }
}
