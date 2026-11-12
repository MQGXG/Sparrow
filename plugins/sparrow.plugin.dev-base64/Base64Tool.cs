using System.Text;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Dev.Base64;

/// <summary>
/// Base64 编解码工具。
///
/// 第二个插件，职责不只是「多一个工具」，而是验证：
/// 加插件不需要改宿主，扫描器 / ALC / 搜索都是通用的。
/// </summary>
public sealed class Base64Tool : ITool
{
    private const string ParameterMode = "mode";
    private const string ParameterUrlSafe = "urlSafe";

    private const string ModeEncode = "编码";
    private const string ModeDecode = "解码";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.dev.base64",
        Name = "Base64 编解码",
        Category = "dev",
        Description = "Base64 编码或解码，支持 URL-safe 变体",
        Keywords = ["base64", "b64", "编码", "解码", "转换"],
        Accepts = InputShape.Text,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.None,
        Order = 20,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterMode,
                Label = "模式",
                Kind = ParameterKind.Choice,
                Choices = [ModeEncode, ModeDecode],
                Default = ModeEncode
            },
            new ToolParameter
            {
                Key = ParameterUrlSafe,
                Label = "URL-safe",
                Kind = ParameterKind.Toggle,
                Default = "false",
                Hint = "用 - 和 _ 替换 + 和 /，并去掉结尾的 ="
            }
        ]
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not TextInput input)
            return Task.FromResult(ToolResult.InvalidInput($"需要文本输入，当前是 {context.Input.GetType().Name}"));

        if (string.IsNullOrWhiteSpace(input.Text))
            return Task.FromResult(ToolResult.InvalidInput("输入为空"));

        var mode = context.GetParameter(ParameterMode, ModeEncode);
        var urlSafe = context.GetParameter(ParameterUrlSafe, false);

        return Task.FromResult(mode == ModeDecode
            ? Decode(input.Text, urlSafe)
            : Encode(input.Text, urlSafe));
    }

    private static ToolResult Encode(string text, bool urlSafe)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var encoded = Convert.ToBase64String(bytes);

        if (urlSafe)
        {
            // URL-safe 变体：+ → -，/ → _，去掉无意义的 = 填充
            encoded = encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        return ToolResult.Ok(new TextPayload(encoded));
    }

    private static ToolResult Decode(string text, bool urlSafe)
    {
        var normalized = text.Trim();

        if (urlSafe)
        {
            normalized = normalized.Replace('-', '+').Replace('_', '/');

            // 补回被 URL-safe 去掉的填充，否则长度不是 4 的倍数
            var remainder = normalized.Length % 4;
            if (remainder == 2)
                normalized += "==";
            else if (remainder == 3)
                normalized += "=";
        }

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            return ToolResult.Ok(new TextPayload(Encoding.UTF8.GetString(bytes)));
        }
        catch (FormatException ex)
        {
            // 解码失败是用户输入的问题：message 给用户看，detail 只进日志
            return ToolResult.InvalidInput("不是合法的 Base64 内容", ex.Message);
        }
    }
}
