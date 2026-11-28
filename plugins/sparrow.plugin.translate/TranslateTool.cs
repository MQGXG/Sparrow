using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Translate;

/// <summary>
/// 翻译工具。
///
/// 在线走宿主注入的 <see cref="IToolHost.Ai"/>；离线走宿主注入的
/// <see cref="IToolHost.LocalTranslation"/>——两者都由宿主实现，插件只描述「翻什么」。
/// 长文本按标点/换行切块，逐块翻译后按原顺序拼接。
/// </summary>
public sealed class TranslateTool : ITool
{
    private const int MaxChunkLength = 3000;
    private const int MaxChunks = 20;

    /// <summary>离线引擎的模型 Id 必须和宿主 LocalModelManager 的清单一致。</summary>
    private const string LocalSmallModel = "hy-mt-1.8b";
    private const string LocalLargeModel = "hy-mt-7b";

    private const string EngineAuto = "自动";
    private const string EngineOnline = "在线模型";
    private const string EngineLocalSmall = "离线 · 1.8B";
    private const string EngineLocalLarge = "离线 · 7B";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.text.translate",
        Name = "翻译",
        Category = "ai",
        Description = "用在线大模型或本地离线模型翻译文本，保留换行与列表",
        Keywords = ["translate", "翻译", "英文", "中文", "日语", "语言", "离线", "本地"],
        Accepts = InputShape.Text,
        Produces = OutputShape.Text,
        // 在线走 AI、离线走本地模型，两条路都行——不能因为「没配 AI」就把工具置灰
        Requires = PlatformCapability.None,
        Order = 40,
        IsHeavy = true,
        Parameters =
        [
            new ToolParameter
            {
                Key = "engine",
                Label = "引擎",
                Kind = ParameterKind.Choice,
                Choices = [EngineAuto, EngineOnline, EngineLocalSmall, EngineLocalLarge],
                Default = EngineAuto,
                Hint = "离线需先在设置里下载模型"
            },
            new ToolParameter
            {
                Key = "to",
                Label = "目标语言",
                Kind = ParameterKind.Choice,
                // 常用 12 种；模型本身支持 33 种，这里只放最常翻的，避免下拉太长
                Choices =
                [
                    "中文", "English", "日本語", "한국어", "Русский", "Français",
                    "Deutsch", "Español", "Português", "العربية", "ไทย", "Tiếng Việt"
                ],
                ChoiceLabels = new Dictionary<string, string>
                {
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
                Default = "English"
            },
            new ToolParameter
            {
                Key = "style",
                Label = "语气",
                Kind = ParameterKind.Choice,
                Choices = ["自然", "正式", "口语"],
                Default = "自然"
            },
            new ToolParameter
            {
                Key = "keepFormatting",
                Label = "保留格式",
                Kind = ParameterKind.Toggle,
                Default = "true",
                Hint = "换行、列表、代码块"
            },
            new ToolParameter
            {
                Key = "glossary",
                Label = "术语表",
                Kind = ParameterKind.Multiline,
                Hint = "每行 原文=译文，如 Sparrow=Sparrow"
            }
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not TextInput input)
            return ToolResult.InvalidInput("需要文本输入");

        if (string.IsNullOrWhiteSpace(input.Text))
            return ToolResult.InvalidInput("没有可翻译的文本");

        var host = context.Host;
        if (host is null)
            return ToolResult.Failed("宿主未注入模型能力");

        var engine = context.GetParameter("engine", EngineAuto);
        var target = context.GetParameter("to", "English");
        var style = context.GetParameter("style", "自然");
        var keepFormatting = context.GetParameter("keepFormatting", "true") != "false";
        var terms = Glossary.Parse(context.GetParameter("glossary", string.Empty));

        // 明确选了离线：不回落在线，缺模型就告诉用户去哪下
        if (engine is EngineLocalSmall or EngineLocalLarge)
        {
            var modelId = engine == EngineLocalLarge ? LocalLargeModel : LocalSmallModel;
            return await TranslateOfflineAsync(
                host, modelId, input.Text, target, style, keepFormatting, terms, cancellationToken);
        }

        // 自动 / 在线：在线模型可用就沿用原来的路径（保持既有行为）
        if (host.Ai.IsAvailable)
        {
            return await TranslateOnlineAsync(
                host, input.Text, target, style, keepFormatting, terms, cancellationToken);
        }

        if (engine == EngineAuto)
        {
            var installed = PickInstalledModel(host);
            if (installed is not null)
            {
                return await TranslateOfflineAsync(
                    host, installed, input.Text, target, style, keepFormatting, terms, cancellationToken);
            }
        }

        return ToolResult.Unsupported(engine == EngineAuto
            ? "请先在「设置 › 模型」里配置 AI 模型，或到「设置 › 模型 › 离线模型」下载一个离线模型"
            : "请先在「设置 › 模型」里配置 AI 模型");
    }

    private static async Task<ToolResult> TranslateOnlineAsync(
        IToolHost host,
        string text,
        string target,
        string style,
        bool keepFormatting,
        IReadOnlyList<LocalTranslationTerm> terms,
        CancellationToken cancellationToken)
    {
        var system = BuildSystemPrompt(target, style, keepFormatting);

        var glossary = BuildGlossary(terms);
        if (glossary.Length > 0)
            system += Environment.NewLine + Environment.NewLine + glossary;

        var chunks = Chunk(text, MaxChunkLength).ToList();
        if (chunks.Count > MaxChunks)
            return ToolResult.InvalidInput($"文本过长（{text.Length} 字），请分段翻译");

        var parts = new List<string>(chunks.Count);
        var previousSource = string.Empty;
        var previousTranslation = string.Empty;

        foreach (var chunk in chunks)
        {
            // 第一块没有上文；后面的块带上上一块的末尾，保持术语和称谓一致
            var contextNote = TranslationContext.Build(previousSource, previousTranslation);
            var requestSystem = contextNote.Length == 0
                ? system
                : system + Environment.NewLine + Environment.NewLine + contextNote;

            try
            {
                var response = await host.Ai.CompleteAsync(
                    new AiRequest
                    {
                        System = requestSystem,
                        Messages = [new AiMessage(AiRole.User, chunk)],
                        Temperature = 0.2,
                        MaxTokens = Math.Clamp(chunk.Length * 2, 512, 4096)
                    },
                    cancellationToken);

                var translated = Clean(response.Text);
                parts.Add(translated);

                previousSource = chunk;
                previousTranslation = translated;
            }
            catch (AiException ex)
            {
                return ex.Kind == AiErrorKind.NotConfigured
                    ? ToolResult.Unsupported(ex.Message)
                    : ToolResult.Failed(ex.Message);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Cancelled();
            }
        }

        return ToolResult.Ok(new TextPayload(string.Concat(parts).Trim()));
    }

    private static async Task<ToolResult> TranslateOfflineAsync(
        IToolHost host,
        string modelId,
        string text,
        string target,
        string style,
        bool keepFormatting,
        IReadOnlyList<LocalTranslationTerm> terms,
        CancellationToken cancellationToken)
    {
        var engine = host.LocalTranslation;

        // 翻译工具不主动下载 1GB+ 的模型：没下就提示去设置页下
        if (engine is null || !engine.IsAvailable(modelId))
        {
            return ToolResult.Unsupported(
                "离线模型尚未下载，请到「设置 › 模型 › 离线模型」下载后再试");
        }

        try
        {
            var translated = await engine.TranslateAsync(
                new LocalTranslationRequest
                {
                    ModelId = modelId,
                    Text = text,
                    TargetLanguage = target,
                    Style = style,
                    KeepFormatting = keepFormatting,
                    Terms = terms
                },
                cancellationToken);

            if (string.IsNullOrWhiteSpace(translated))
                return ToolResult.Failed("离线翻译失败：模型不可用或文本过长");

            return ToolResult.Ok(new TextPayload(translated));
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Cancelled();
        }
        catch (Exception ex)
        {
            return ToolResult.Failed($"离线翻译失败：{ex.Message}");
        }
    }

    /// <summary>「自动」且在线不可用时，挑第一个已下载的模型（顺序由宿主清单决定）。</summary>
    private static string? PickInstalledModel(IToolHost host)
    {
        var models = host.LocalModels;
        if (models is null)
            return null;

        foreach (var model in models.All)
        {
            if (models.IsInstalled(model.Id))
                return model.Id;
        }

        return null;
    }

    private static string BuildSystemPrompt(string target, string style, bool keepFormatting)
    {
        var formatting = keepFormatting
            ? "保留原文的换行、列表、编号、代码块和 Markdown 结构。"
            : "只输出连续段落，不要保留复杂排版。";

        return $"""
            你是专业翻译。把用户提供的文本翻译成{target}。
            语气：{style}。
            {formatting}
            只输出译文，不要解释、不要加引号、不要加“译文：”前缀。
            """;
    }

    /// <summary>在线路径自己拼术语表；离线路径交给宿主的翻译引擎。</summary>
    private static string BuildGlossary(IReadOnlyList<LocalTranslationTerm> terms)
    {
        if (terms.Count == 0)
            return string.Empty;

        return "术语表（这些词必须按指定译法翻译）：\n"
            + string.Join('\n', terms.Select(term => $"{term.Source} → {term.Target}"))
            + "\n";
    }

    /// <summary>按长度切块，优先在换行/句末标点处断开，避免把句子劈开。</summary>
    private static IEnumerable<string> Chunk(string text, int maxLength)
    {
        var start = 0;

        while (start < text.Length)
        {
            var length = Math.Min(maxLength, text.Length - start);

            if (start + length < text.Length)
            {
                var cut = text.LastIndexOfAny(
                    ['\n', '。', '！', '？', '.', '!', '?', '；', ';'],
                    start + length - 1,
                    length);

                if (cut > start + maxLength / 3)
                    length = cut - start + 1;
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }

    /// <summary>有些模型会自作主张加一对引号，去掉。</summary>
    private static string Clean(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '“' && trimmed[^1] == '”')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }
}
