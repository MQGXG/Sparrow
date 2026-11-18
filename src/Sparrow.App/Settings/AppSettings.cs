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

    /// <summary>剪贴板快取面板热键，格式 "Ctrl+Alt+B"；只呼出搜索 + 列表，不带主窗框架。</summary>
    public string ClipboardPanelHotkey { get; init; } = "Ctrl+Alt+B";

    /// <summary>
    /// 工具热键：工具 ID → 热键，例如 <c>"sparrow.window.pin": "Ctrl+Alt+P"</c>。
    /// 按下直接执行该工具（不打开面板）；工具被禁用时热键不会注册。
    /// </summary>
    public Dictionary<string, string> ToolHotkeys { get; init; } = [];

    /// <summary>
    /// 托盘菜单自定义顺序与内容；空数组 = 使用内置默认菜单。
    /// 条目可以是分隔线、内置动作或工具 ID，见 <see cref="TrayMenuItemConfig"/>。
    /// </summary>
    public List<TrayMenuItemConfig> TrayMenu { get; init; } = [];

    /// <summary>历史最多保留多少条；默认 500，和 M1 行为一致。</summary>
    public int HistoryMaxEntries { get; init; } = 500;

    /// <summary>历史保留天数；0 表示不按天数清理。</summary>
    public int HistoryRetentionDays { get; init; }

    /// <summary>是否记录剪贴板历史；默认开启，可随时关闭（隐私）。</summary>
    public bool ClipboardHistoryEnabled { get; init; } = true;

    /// <summary>剪贴板历史最多保留多少条；图片另存为 PNG 文件。</summary>
    public int ClipboardHistoryMaxEntries { get; init; } = 200;

    /// <summary>剪贴板历史保留天数；0 表示不按天数清理。</summary>
    public int ClipboardHistoryRetentionDays { get; init; }

    /// <summary>剪贴板历史总容量上限（字节）；0 表示不限制。收藏条目不参与清理。</summary>
    public long ClipboardHistoryMaxBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>不记录这些来源进程名（如 chrome、1Password）；只影响之后的复制。</summary>
    public List<string> ClipboardExcludedApps { get; init; } = [];

    /// <summary>截图 OCR 识别框显示方式：all / hover / off。</summary>
    public string CaptureOcrBoxMode { get; init; } = "hover";

    /// <summary>顺序粘贴热键：在目标应用里按下，写入队列下一条并模拟 Ctrl+V。</summary>
    public string QueuePasteHotkey { get; init; } = "Ctrl+Alt+V";

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
