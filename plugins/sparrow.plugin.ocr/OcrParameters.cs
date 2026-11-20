using Sparrow.Plugin.Sdk;

namespace Sparrow.Plugin.Ocr;

/// <summary>
/// 单张与批量共用的参数声明与解析。
/// 放在一处，避免两个工具的选项文案/默认值慢慢分叉。
/// </summary>
internal static class OcrParameters
{
    public const string LanguageKey = "language";
    public const string LayoutKey = "layout";

    public const string LayoutLine = "按行";
    public const string LayoutRaw = "原文";
    public const string LayoutPosition = "按位置";
    public const string LayoutTable = "表格";

    public static ToolParameter Language { get; } = new()
    {
        Key = LanguageKey,
        Label = "语言",
        Kind = ParameterKind.Choice,
        Choices = ["ch", "en", "ja", "ko", "ru", "uk", "ar", "fa", "bg", "sr", "hi", "th", "el", "fr", "de", "es"],
        // 值是 PaddleOCR 的模型代码，直接显示对中文用户没有意义；
        // 值不变，只是界面显示中文名（代码放在括号里方便对照）
        ChoiceLabels = new Dictionary<string, string>
        {
            ["ch"] = "中文（ch）",
            ["en"] = "英语（en）",
            ["ja"] = "日语（ja）",
            ["ko"] = "韩语（ko）",
            ["ru"] = "俄语（ru）",
            ["uk"] = "乌克兰语（uk）",
            ["ar"] = "阿拉伯语（ar）",
            ["fa"] = "波斯语（fa）",
            ["bg"] = "保加利亚语（bg）",
            ["sr"] = "塞尔维亚语（sr）",
            ["hi"] = "印地语（hi）",
            ["th"] = "泰语（th）",
            ["el"] = "希腊语（el）",
            ["fr"] = "法语（fr）",
            ["de"] = "德语（de）",
            ["es"] = "西班牙语（es）"
        },
        Default = "ch",
        Hint = "ch 包含简体、繁体、拼音、英日；ru 覆盖俄语/乌克兰/白俄"
    };

    public static ToolParameter Layout { get; } = new()
    {
        Key = LayoutKey,
        Label = "整理模式",
        Kind = ParameterKind.Choice,
        Choices = [LayoutLine, LayoutRaw, LayoutPosition, LayoutTable],
        Default = LayoutLine,
        Hint = "表格：Tab 分隔，可直接粘贴进 Excel；按位置：空格还原排版"
    };

    /// <summary>整理模式参数 → 枚举；未知值回退默认「按行」。</summary>
    public static OcrLayoutMode ResolveLayout(ToolContext context) =>
        context.GetParameter(LayoutKey, LayoutLine) switch
        {
            LayoutRaw => OcrLayoutMode.Raw,
            LayoutPosition => OcrLayoutMode.Position,
            LayoutTable => OcrLayoutMode.Table,
            _ => OcrLayoutMode.Line
        };

    /// <summary>worker 固定在插件目录的 worker 子目录下。</summary>
    public static string WorkerPath(IToolHost host) =>
        Path.Combine(host.PluginDirectory, "worker", "Sparrow.Ocr.Worker.exe");
}
