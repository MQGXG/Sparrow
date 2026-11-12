namespace Sparrow.App.Settings;

/// <summary>
/// 用户设置。
///
/// M1 只有「禁用插件」一项，后续字段加在同一个文件里，靠 <see cref="SchemaVersion"/> 迁移。
/// 设置文件损坏时回退默认值，绝不让宿主起不来。
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>被禁用的插件 ID（等于插件目录名）。命中的插件不扫描、不加载、不进搜索结果。</summary>
    public string[] DisabledPlugins { get; init; } = [];
}
