using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Svg.Prompt;

/// <summary>
/// 提示词生成 SVG。
///
/// 插件只负责「把需求变成 SVG 源码」；渲染、预览、清洗、保存都由宿主完成。
/// 模型输出不可信，宿主渲染前还会再做一次安全清洗。
/// </summary>
public sealed class SvgPromptTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.svg.prompt",
        Name = "提示词生成 SVG",
        Category = "ai",
        Description = "用一句话描述生成矢量 SVG，结果可直接预览",
        Keywords = ["svg", "矢量", "图标", "logo", "生成", "绘图", "ai"],
        Accepts = InputShape.Text,
        Produces = OutputShape.Svg,
        Requires = PlatformCapability.Ai,
        Order = 41,
        Parameters =
        [
            new ToolParameter
            {
                Key = "style",
                Label = "风格",
                Kind = ParameterKind.Choice,
                Choices = ["扁平", "线性", "等距", "手绘"],
                Default = "扁平"
            },
            new ToolParameter
            {
                Key = "size",
                Label = "画布",
                Kind = ParameterKind.Choice,
                Choices = ["256", "512", "1024"],
                Default = "512"
            },
            new ToolParameter
            {
                Key = "background",
                Label = "背景",
                Kind = ParameterKind.Choice,
                Choices = ["透明", "白色", "深色"],
                Default = "透明"
            }
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not TextInput input)
            return ToolResult.InvalidInput("需要文本输入");

        if (string.IsNullOrWhiteSpace(input.Text))
            return ToolResult.InvalidInput("请先描述你想画什么");

        if (context.Host is null)
            return ToolResult.Failed("宿主未注入模型能力");

        if (!context.Host.Ai.IsAvailable)
            return ToolResult.Unsupported("请先在「设置 › 模型」里配置 AI 模型");

        var style = context.GetParameter("style", "扁平");
        var size = int.TryParse(context.GetParameter("size", "512"), out var parsedSize)
            ? Math.Clamp(parsedSize, 128, 1024)
            : 512;
        var background = context.GetParameter("background", "透明");

        try
        {
            var response = await context.Host.Ai.CompleteAsync(
                new AiRequest
                {
                    System = BuildSystemPrompt(style, size, background),
                    Messages = [new AiMessage(AiRole.User, input.Text.Trim())],
                    Temperature = 0.6,
                    MaxTokens = 4096
                },
                cancellationToken);

            var svg = ExtractSvg(response.Text);
            if (svg is null)
                return ToolResult.Failed("模型没有返回 SVG，请换个描述再试");

            return ToolResult.Ok(new SvgPayload(svg)
            {
                Width = size,
                Height = size
            });
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

    private static string BuildSystemPrompt(string style, int size, string background) => $"""
        你是矢量图形设计师。只输出一个完整的 <svg>…</svg>，不要 Markdown 代码围栏，不要解释。
        硬性要求：
        1. 根元素包含 xmlns="http://www.w3.org/2000/svg"，viewBox="0 0 {size} {size}"。
        2. 只用 path / rect / circle / ellipse / line / polygon / linearGradient / radialGradient。
        3. 禁止 script、foreignObject、image、外链资源、事件属性、外部 CSS。
        4. 风格：{style}；背景：{background}；构图居中、留白充足。
        5. 元素总数控制在 300 以内，路径数据尽量精炼。
        """;

    /// <summary>从模型输出里截出第一个完整 &lt;svg&gt; 块，容忍 Markdown 围栏和前后解释。</summary>
    private static string? ExtractSvg(string text)
    {
        var start = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var end = text.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (end < start)
            return null;

        return text.Substring(start, end - start + "</svg>".Length).Trim();
    }
}
