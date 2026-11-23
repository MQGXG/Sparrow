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
    public const string TranslateKey = "translate";
    public const string ReuseKey = "reuse";
    public const string EngineKey = "engine";

    /// <summary>自动：在线模型优先，没配就用第一个已下载的本地模型。</summary>
    public const string EngineAuto = "auto";

    /// <summary>只用在线模型。</summary>
    public const string EngineOnline = "ai";

    /// <summary>只用本地模型（截图工具条可传 <c>local:{模型 Id}</c> 指定具体模型）。</summary>
    public const string EngineLocal = "local";

    /// <summary>识别结束把 worker 留 60 秒（默认）：连续识别快，空闲自动退出。</summary>
    public const string ReuseAuto = "auto";

    /// <summary>每次识别结束立即退出 worker：最省内存，代价是每次 1–3 秒模型初始化。</summary>
    public const string ReuseOff = "off";

    /// <summary>不翻译：默认值，避免每张图都白跑一次模型。</summary>
    public const string TranslateNone = "none";

    public const string LayoutLine = "按行";
    public const string LayoutRaw = "原文";
    public const string LayoutPosition = "按位置";
    public const string LayoutTable = "表格";

    public static ToolParameter Language { get; } = new()
    {
        Key = LanguageKey,
        Label = "语言",
        Kind = ParameterKind.Choice,
        Choices = [
            "ch", "en", "ja", "ko", "ru", "uk", "ar", "fa",
            "bg", "sr", "hi", "th", "el", "fr", "de", "es",
            // 下面这些模型早就内置在 Models.LocalV5 里，只是之前没暴露给下拉：
            // it/pt/nl/pl/tr/vi 和 fr/de/es 共用 LatinV5，te/ta 是独立模型
            "it", "pt", "nl", "pl", "tr", "vi", "te", "ta"
        ],
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
            ["es"] = "西班牙语（es）",
            ["it"] = "意大利语（it）",
            ["pt"] = "葡萄牙语（pt）",
            ["nl"] = "荷兰语（nl）",
            ["pl"] = "波兰语（pl）",
            ["tr"] = "土耳其语（tr）",
            ["vi"] = "越南语（vi）",
            ["te"] = "泰卢固语（te）",
            ["ta"] = "泰米尔语（ta）"
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

    /// <summary>识别文本的换行整理；只影响复制出去的文本，不改行框。</summary>
    public static ToolParameter Formatting { get; } = new()
    {
        Key = OcrTextTransform.FormattingKey,
        Label = "格式",
        Kind = ParameterKind.Choice,
        Choices =
        [
            OcrTextTransform.FormatNone,
            OcrTextTransform.FormatKeep,
            OcrTextTransform.FormatRemove,
            OcrTextTransform.FormatSmart
        ],
        ChoiceLabels = new Dictionary<string, string>
        {
            [OcrTextTransform.FormatNone] = "不处理",
            [OcrTextTransform.FormatKeep] = "保留换行",
            [OcrTextTransform.FormatRemove] = "去掉换行",
            [OcrTextTransform.FormatSmart] = "智能排版"
        },
        Default = OcrTextTransform.FormatKeep,
        Hint = "智能排版：保留句末换行，把被硬折行的句子接回去"
    };

    /// <summary>标点半角 / 全角统一；同样只影响复制出去的文本。</summary>
    public static ToolParameter Punctuation { get; } = new()
    {
        Key = OcrTextTransform.PunctuationKey,
        Label = "标点",
        Kind = ParameterKind.Choice,
        Choices =
        [
            OcrTextTransform.PunctuationNone,
            OcrTextTransform.PunctuationHalf,
            OcrTextTransform.PunctuationFull
        ],
        ChoiceLabels = new Dictionary<string, string>
        {
            [OcrTextTransform.PunctuationNone] = "不处理",
            [OcrTextTransform.PunctuationHalf] = "半角",
            [OcrTextTransform.PunctuationFull] = "全角"
        },
        Default = OcrTextTransform.PunctuationNone,
        Hint = "把逗号、句号、括号等统一成半角或全角"
    };

    /// <summary>翻译引擎：自动（在线优先）/ 在线 / 本地。截图工具条可传 local:{模型Id}。</summary>
    public static ToolParameter Engine { get; } = new()
    {
        Key = EngineKey,
        Label = "引擎",
        Kind = ParameterKind.Choice,
        Choices = [EngineAuto, EngineOnline, EngineLocal],
        ChoiceLabels = new Dictionary<string, string>
        {
            [EngineAuto] = "自动（在线优先）",
            [EngineOnline] = "在线模型",
            [EngineLocal] = "本地模型"
        },
        Default = EngineAuto,
        Hint = "自动：有在线模型就先用；否则用第一个已下载的本地模型"
    };

    /// <summary>
    /// 识别后可选翻译：结果按「原文 / 译文」对照输出，方便核对。
    /// 需要先在设置里配好模型；没配时静默跳过，识别结果照常返回。
    /// </summary>
    public static ToolParameter Translate { get; } = new()
    {
        Key = TranslateKey,
        Label = "翻译为",
        Kind = ParameterKind.Choice,
        // 跟随翻译工具的常用 12 种；离线时用宿主的本地翻译引擎
        Choices =
        [
            TranslateNone, "中文", "English", "日本語", "한국어", "Русский", "Français",
            "Deutsch", "Español", "Português", "العربية", "ไทย", "Tiếng Việt"
        ],
        ChoiceLabels = new Dictionary<string, string>
        {
            [TranslateNone] = "不翻译",
            ["中文"] = "中文",
            ["English"] = "英语",
            ["日本語"] = "日语",
            ["한국어"] = "韩语",
            ["Русский"] = "俄语",
            ["Français"] = "法语",
            ["Deutsch"] = "德语",
            ["Español"] = "西班牙语",
            ["Português"] = "葡萄牙语",
            ["العربية"] = "阿拉伯语",
            ["ไทย"] = "泰语",
            ["Tiếng Việt"] = "越南语"
        },
        Default = TranslateNone,
        Hint = "识别后翻译，结果按「原文 / 译文」对照输出；在线模型或已下载的离线模型均可"
    };

    /// <summary>识别结束后是否复用 worker：默认复用 60 秒，连续识别更快。</summary>
    public static ToolParameter Reuse { get; } = new()
    {
        Key = ReuseKey,
        Label = "复用 worker",
        Kind = ParameterKind.Choice,
        Choices = [ReuseAuto, ReuseOff],
        ChoiceLabels = new Dictionary<string, string>
        {
            [ReuseAuto] = "自动（快）",
            [ReuseOff] = "关闭（省内存）"
        },
        Default = ReuseAuto,
        Hint = "自动：识别后保留 worker 60 秒，连续识别省下模型初始化；关闭：每次用完立即退出"
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

    /// <summary>格式参数 → 取值；未知值回退「保留换行」。</summary>
    public static string ResolveFormatting(ToolContext context) =>
        context.GetParameter(OcrTextTransform.FormattingKey, OcrTextTransform.FormatKeep) switch
        {
            OcrTextTransform.FormatNone => OcrTextTransform.FormatNone,
            OcrTextTransform.FormatRemove => OcrTextTransform.FormatRemove,
            OcrTextTransform.FormatSmart => OcrTextTransform.FormatSmart,
            _ => OcrTextTransform.FormatKeep
        };

    /// <summary>标点参数 → 取值；未知值回退「不处理」。</summary>
    public static string ResolvePunctuation(ToolContext context) =>
        context.GetParameter(OcrTextTransform.PunctuationKey, OcrTextTransform.PunctuationNone) switch
        {
            OcrTextTransform.PunctuationHalf => OcrTextTransform.PunctuationHalf,
            OcrTextTransform.PunctuationFull => OcrTextTransform.PunctuationFull,
            _ => OcrTextTransform.PunctuationNone
        };

    /// <summary>
    /// 引擎参数 → 取值。允许 <c>local:{模型Id}</c> 指定具体本地模型；
    /// 未知值回退「自动」。
    /// </summary>
    public static string ResolveEngine(ToolContext context)
    {
        var value = context.GetParameter(EngineKey, EngineAuto);

        if (value is EngineOnline or EngineAuto)
            return value;

        if (value == EngineLocal || value.StartsWith("local:", StringComparison.Ordinal))
            return value;

        return EngineAuto;
    }

    /// <summary>从引擎取值里取出指定的本地模型 Id；没指定返回 null。</summary>
    public static string? LocalModelId(string engine) =>
        engine.StartsWith("local:", StringComparison.Ordinal) && engine.Length > 6
            ? engine[6..]
            : null;

    /// <summary>worker 固定在插件目录的 worker 子目录下。</summary>
    public static string WorkerPath(IToolHost host) =>
        Path.Combine(host.PluginDirectory, "worker", "Sparrow.Ocr.Worker.exe");

    public const string OutputKey = "output";
    public const string OutputText = "text";
    public const string OutputTableHtml = "table-html";
    public const string OutputTableMarkdown = "table-md";

    /// <summary>识别类型：文字（本地 OCR）或表格（视觉模型输出 HTML / Markdown）。</summary>
    public static ToolParameter Output { get; } = new()
    {
        Key = OutputKey,
        Label = "识别类型",
        Kind = ParameterKind.Choice,
        Choices = [OutputText, OutputTableHtml, OutputTableMarkdown],
        ChoiceLabels = new Dictionary<string, string>
        {
            [OutputText] = "文字",
            [OutputTableHtml] = "表格 · HTML",
            [OutputTableMarkdown] = "表格 · Markdown"
        },
        Default = OutputText,
        Hint = "表格需要支持图片的视觉模型"
    };
}
