namespace Sparrow.App.Plugins;

/// <summary>
/// 扫描结果。要么有效（<see cref="Manifest"/> 非空），要么带着可读的失败原因。
///
/// 关键：这个对象**不持有任何程序集引用**。扫描阶段只读文件，
/// 保证「冷启动不加载插件程序集」这条约束（见 docs/plugin-model.md 第六节）。
/// </summary>
public sealed record PluginCandidate(
    string Directory,
    string DirectoryName,
    string? ManifestPath,
    PluginManifest? Manifest,
    string? Error)
{
    public bool IsValid => Manifest is not null && Error is null;

    /// <summary>展示用 ID：清单无效时退回目录名，避免日志里出现空字符串。</summary>
    public string Id => Manifest?.Id ?? DirectoryName;

    /// <summary>入口程序集绝对路径。清单无效时返回空串。</summary>
    public string EntryPath => Manifest is null
        ? string.Empty
        : Path.Combine(Directory, Manifest.EntryAssembly);
}
