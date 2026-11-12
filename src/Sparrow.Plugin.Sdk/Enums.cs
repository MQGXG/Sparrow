namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 工具接受的输入形态。决定这个工具能接在谁后面（见 docs/tool-catalog.md 的矩阵）。
/// </summary>
public enum InputShape
{
    None = 0,
    Text,
    RichText,
    Image,
    Files,
    Selection
}

/// <summary>
/// 工具产出的形态。决定这个工具的结果能喂给谁。
/// </summary>
public enum OutputShape
{
    None = 0,
    Text,
    Svg,
    Image,
    FileSet,
    Table
}

/// <summary>
/// 输入来源。**只用于展示和日志，不得用于分支逻辑**——分支只看 Input 的具体类型。
/// </summary>
public enum ToolSource
{
    Manual = 0,
    Clipboard,
    Selection,
    Screenshot,
    Dropped,
    PreviousTool
}

/// <summary>
/// 平台能力。由宿主探测，插件只声明 Requires，不自己判断操作系统。
/// </summary>
[Flags]
public enum PlatformCapability
{
    None = 0,
    Clipboard = 1 << 0,
    /// <summary>平台上存在剪贴板变更事件（macOS 为 false，需轮询）。</summary>
    ClipboardMonitor = 1 << 1,
    Screenshot = 1 << 2,
    Selection = 1 << 3,
    GlobalHotkey = 1 << 4,
    Ocr = 1 << 5,
    SvgRender = 1 << 6,
    Network = 1 << 7
}

/// <summary>
/// 参数控件的类型。宿主据此自动生成参数面板，插件不提供任何 UI。
/// </summary>
public enum ParameterKind
{
    Text = 0,
    Multiline,
    Number,
    Toggle,
    Choice,
    FilePath,
    Color
}

/// <summary>
/// 失败类型。**InvalidInput 和 Execution 必须区分**：
/// 前者是用户的问题（告诉他怎么改），后者是我们的问题（显示错误码 + 日志入口）。
/// </summary>
public enum ToolErrorKind
{
    InvalidInput = 0,
    Execution,
    Timeout,
    Cancelled,
    PlatformUnsupported,
    NotImplemented
}

/// <summary>
/// 结果卡片底部的统一动作。
/// </summary>
public enum ToolActionKind
{
    Copy = 0,
    Save,
    Pin,
    SendTo,
    OpenLocation
}

/// <summary>
/// 位图格式。宿主负责解码，插件只声明数据是什么。
/// </summary>
public enum ImageFormat
{
    Unknown = 0,
    Png,
    Jpeg,
    Webp,
    Bmp,
    Gif,
    Svg
}
