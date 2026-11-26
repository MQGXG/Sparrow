namespace Sparrow.App.Services;

/// <summary>
/// 应用数据路径的统一出口。
///
/// 分三类：
/// - <see cref="ConfigRoot"/>：启动早期就要读的配置和日志，固定在
///   %LOCALAPPDATA%\Sparrow，不能依赖设置里的数据目录（否则鸡生蛋）。
/// - <see cref="DataRoot"/>：剪贴板、历史这类默认数据，跟随设置里的根目录。
/// - <see cref="ModelRoot"/> / <see cref="RecordRoot"/>：可以单独指到别的盘，
///   留空时回落到 <see cref="DataRoot"/> 下的子目录。
/// </summary>
internal static class SparrowPaths
{
    /// <summary>配置与日志的固定根目录。</summary>
    public static string ConfigRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sparrow");

    /// <summary>默认数据根目录（剪贴板历史、历史记录）。</summary>
    public static string DataRoot { get; private set; } = ConfigRoot;

    /// <summary>模型目录；空 = <see cref="DataRoot"/> 下的 models。</summary>
    public static string ModelRoot { get; private set; } = string.Empty;

    /// <summary>录制目录；空 = <see cref="DataRoot"/> 下的 recordings。</summary>
    public static string RecordRoot { get; private set; } = string.Empty;

    public static string Logs => Path.Combine(ConfigRoot, "logs");

    /// <summary>Ffmpeg 体积小、下载逻辑也依赖固定位置，不跟随数据目录。</summary>
    public static string FfmpegCache => Path.Combine(ConfigRoot, "ffmpeg");

    public static string ClipboardDb => Path.Combine(DataRoot, "clipboard.db");

    public static string HistoryFile => Path.Combine(DataRoot, "history.jsonl");

    public static string Models =>
        string.IsNullOrWhiteSpace(ModelRoot) ? Path.Combine(DataRoot, "models") : ModelRoot;

    public static string Recordings =>
        string.IsNullOrWhiteSpace(RecordRoot) ? Path.Combine(DataRoot, "recordings") : RecordRoot;

    /// <summary>空或无效值回落默认目录。</summary>
    public static string ResolveDataRoot(string? preferred) =>
        string.IsNullOrWhiteSpace(preferred) ? ConfigRoot : preferred;

    /// <summary>启动时套用设置里的目录；空值表示用默认。</summary>
    public static void Apply(string? dataDirectory, string? modelDirectory, string? recordDirectory)
    {
        DataRoot = ResolveDataRoot(dataDirectory);
        ModelRoot = modelDirectory ?? string.Empty;
        RecordRoot = recordDirectory ?? string.Empty;
    }
}
