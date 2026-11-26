namespace Sparrow.App.Services;

/// <summary>
/// 应用数据路径的统一出口。
///
/// 分两类：
/// - <see cref="ConfigRoot"/>：启动早期就要读的配置和日志，固定在
///   %LOCALAPPDATA%\Sparrow，不能依赖设置里的数据目录（否则鸡生蛋）。
/// - <see cref="DataRoot"/>：剪贴板、模型、录制、历史这类会变大的数据，
///   跟随设置里的「数据目录」，用户可以换到别的盘。
/// </summary>
internal static class SparrowPaths
{
    /// <summary>配置与日志的固定根目录。</summary>
    public static string ConfigRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sparrow");

    /// <summary>可变数据根目录；默认与 <see cref="ConfigRoot"/> 相同。</summary>
    public static string DataRoot { get; set; } = ConfigRoot;

    public static string Logs => Path.Combine(ConfigRoot, "logs");

    /// <summary>Ffmpeg 体积小、下载逻辑也依赖固定位置，不跟随数据目录。</summary>
    public static string FfmpegCache => Path.Combine(ConfigRoot, "ffmpeg");

    public static string ClipboardDb => Path.Combine(DataRoot, "clipboard.db");

    public static string Models => Path.Combine(DataRoot, "models");

    public static string Recordings => Path.Combine(DataRoot, "recordings");

    public static string HistoryFile => Path.Combine(DataRoot, "history.jsonl");

    /// <summary>空或无效值回落默认目录。</summary>
    public static string ResolveDataRoot(string? preferred) =>
        string.IsNullOrWhiteSpace(preferred) ? ConfigRoot : preferred;

    /// <summary>启动时应用设置里的数据目录（传空表示用默认）。</summary>
    public static void ApplyDataRoot(string? preferred) =>
        DataRoot = ResolveDataRoot(preferred);
}
