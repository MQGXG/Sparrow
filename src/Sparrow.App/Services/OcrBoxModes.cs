namespace Sparrow.App.Services;

/// <summary>截图 OCR 识别框的显示方式。</summary>
public enum OcrBoxMode
{
    /// <summary>全部显示。</summary>
    All,

    /// <summary>默认不显示，鼠标靠近哪一行只亮哪一行。</summary>
    Hover,

    /// <summary>完全不显示，也不参与点行复制。</summary>
    Off
}

/// <summary>
/// 识别框模式的解析 / 展示 / 切换；纯函数，便于测试与持久化。
/// </summary>
public static class OcrBoxModes
{
    public const string All = "all";
    public const string Hover = "hover";
    public const string Off = "off";

    public static OcrBoxMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        All => OcrBoxMode.All,
        Off => OcrBoxMode.Off,
        _ => OcrBoxMode.Hover
    };

    public static string Format(OcrBoxMode mode) => mode switch
    {
        OcrBoxMode.All => All,
        OcrBoxMode.Off => Off,
        _ => Hover
    };

    public static OcrBoxMode Next(OcrBoxMode mode) => mode switch
    {
        OcrBoxMode.All => OcrBoxMode.Hover,
        OcrBoxMode.Hover => OcrBoxMode.Off,
        _ => OcrBoxMode.All
    };

    public static string Label(OcrBoxMode mode) => mode switch
    {
        OcrBoxMode.All => "框 · 全部",
        OcrBoxMode.Off => "框 · 关闭",
        _ => "框 · 悬停"
    };
}
