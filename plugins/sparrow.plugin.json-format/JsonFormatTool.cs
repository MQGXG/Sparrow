using System.Text.Encodings.Web;
using System.Text.Json;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Json.Format;

/// <summary>
/// JSON 格式化工具。
///
/// 这个工具是插件契约的第一个真实使用者，同时承担两个职责：
/// 1. 验证 <c>ITool</c> 契约是否好用（如果写起来别扭，说明契约有问题）
/// 2. 验证插件项目的产出物里不含 SDK dll
/// </summary>
public sealed class JsonFormatTool : ITool
{
    private const string ParameterMode = "mode";
    private const string ParameterIndent = "indent";

    private const string ModeFormat = "格式化";
    private const string ModeMinify = "压缩";
    private const string ModeValidate = "校验";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.json.format",
        Name = "JSON 格式化",
        Category = "json",
        Description = "格式化、压缩或校验 JSON，出错时定位到行列",
        Keywords = ["json", "格式化", "压缩", "美化", "校验", "beautify"],
        Accepts = InputShape.Text,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.None,
        Order = 10,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterMode,
                Label = "模式",
                Kind = ParameterKind.Choice,
                Choices = [ModeFormat, ModeMinify, ModeValidate],
                Default = ModeFormat
            },
            new ToolParameter
            {
                Key = ParameterIndent,
                Label = "缩进",
                Kind = ParameterKind.Choice,
                Choices = ["2", "4", "Tab"],
                Default = "2",
                Hint = "仅「格式化」模式生效"
            }
        ]
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        // 输入形态不匹配是用户能理解的问题，返回 InvalidInput 而不是抛异常
        if (context.Input is not TextInput input)
            return Task.FromResult(ToolResult.InvalidInput($"需要文本输入，当前是 {context.Input.GetType().Name}"));

        if (string.IsNullOrWhiteSpace(input.Text))
            return Task.FromResult(ToolResult.InvalidInput("输入为空"));

        var mode = context.GetParameter(ParameterMode, ModeFormat);

        try
        {
            using var document = JsonDocument.Parse(input.Text);
            cancellationToken.ThrowIfCancellationRequested();

            var payload = mode switch
            {
                ModeMinify => new TextPayload(Minify(document.RootElement)),
                ModeValidate => new TextPayload("JSON 合法"),
                _ => new TextPayload(Pretty(document.RootElement, context.GetParameter(ParameterIndent, "2")))
            };

            return Task.FromResult(ToolResult.Ok(payload));
        }
        catch (JsonException ex)
        {
            // 行列信息是给用户看的，所以进 message；
            // 原始异常信息可能包含内部路径，只进 detail（日志）。
            var line = (ex.LineNumber ?? 0) + 1;
            var column = (ex.BytePositionInLine ?? 0) + 1;

            return Task.FromResult(ToolResult.InvalidInput(
                $"JSON 语法错误：第 {line} 行，第 {column} 列",
                ex.Message));
        }
    }

    private static string Minify(JsonElement element) =>
        JsonSerializer.Serialize(element, MinifyOptions);

    private static string Pretty(JsonElement element, string indent)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        if (string.Equals(indent, "Tab", StringComparison.OrdinalIgnoreCase))
        {
            options.IndentCharacter = '\t';
            options.IndentSize = 1;
        }
        else
        {
            options.IndentSize = int.TryParse(indent, out var size) && size > 0 ? size : 2;
        }

        return JsonSerializer.Serialize(element, options);
    }

    // 复用同一个 options 实例，避免每次调用都构造（JsonSerializerOptions 首次使用后有缓存）
    private static readonly JsonSerializerOptions MinifyOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
