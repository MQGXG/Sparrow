using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Translate;

/// <summary>
/// AI 翻译工具。
///
/// 插件不关心 provider、端点或密钥：全部通过 <see cref="IToolHost.Ai"/>。
/// 长文本按标点/换行切块，逐块翻译后按原顺序拼接。
/// </summary>
public sealed class TranslateTool : ITool
{
    private const int MaxChunkLength = 3000;
    private const int MaxChunks = 20;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.text.translate",
        Name = "翻译",
        Category = "ai",
        Description = "用已配置的大模型翻译文本，保留换行与列表",
        Keywords = ["translate", "翻译", "英文", "中文", "日语", "语言"],
        Accepts = InputShape.Text,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.Ai,
        Order = 40,
        Parameters =
        [
            new ToolParameter
            {
                Key = "to",
                Label = "目标语言",
                Kind = ParameterKind.Choice,
                Choices = ["中文", "English", "日本語", "한국어", "Русский", "Français", "Deutsch", "Español"],
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
            }
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not TextInput input)
            return ToolResult.InvalidInput("需要文本输入");

        if (string.IsNullOrWhiteSpace(input.Text))
            return ToolResult.InvalidInput("没有可翻译的文本");

        if (context.Host is null)
            return ToolResult.Failed("宿主未注入模型能力");

        if (!context.Host.Ai.IsAvailable)
            return ToolResult.Unsupported("请先在「设置 › 模型」里配置 AI 模型");

        var target = context.GetParameter("to", "English");
        var style = context.GetParameter("style", "自然");
        var keepFormatting = context.GetParameter("keepFormatting", "true") != "false";
        var system = BuildSystemPrompt(target, style, keepFormatting);

        var chunks = Chunk(input.Text, MaxChunkLength).ToList();
        if (chunks.Count > MaxChunks)
            return ToolResult.InvalidInput($"文本过长（{input.Text.Length} 字），请分段翻译");

        var parts = new List<string>(chunks.Count);

        foreach (var chunk in chunks)
        {
            try
            {
                var response = await context.Host.Ai.CompleteAsync(
                    new AiRequest
                    {
                        System = system,
                        Messages = [new AiMessage(AiRole.User, chunk)],
                        Temperature = 0.2,
                        MaxTokens = Math.Clamp(chunk.Length * 2, 512, 4096)
                    },
                    cancellationToken);

                parts.Add(Clean(response.Text));
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
