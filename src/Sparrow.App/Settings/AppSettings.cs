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

    /// <summary>搜索面板热键，格式 "Ctrl+Alt+S"。</summary>
    public string SearchHotkey { get; init; } = "Ctrl+Alt+S";

    /// <summary>截图识别热键，格式 "Ctrl+Alt+C"。</summary>
    public string CaptureHotkey { get; init; } = "Ctrl+Alt+C";

    /// <summary>历史最多保留多少条；默认 500，和 M1 行为一致。</summary>
    public int HistoryMaxEntries { get; init; } = 500;

    /// <summary>历史保留天数；0 表示不按天数清理。</summary>
    public int HistoryRetentionDays { get; init; }

    /// <summary>AI 是否启用；默认关闭，用户显式配置后才可用。</summary>
    public bool AiEnabled { get; init; }

    /// <summary>provider 预设键：openai / deepseek / ollama / custom…</summary>
    public string AiProvider { get; init; } = "ollama";

    /// <summary>OpenAI 兼容端点，如 http://localhost:11434/v1。</summary>
    public string AiBaseUrl { get; init; } = "http://localhost:11434/v1";

    /// <summary>模型名。</summary>
    public string AiModel { get; init; } = "qwen2.5vl:7b";

    public double AiTemperature { get; init; } = 0.2;

    public int AiMaxTokens { get; init; } = 2048;
}
