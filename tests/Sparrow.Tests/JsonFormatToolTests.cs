using Shouldly;
using Sparrow.Plugin.Json.Format;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

public sealed class JsonFormatToolTests
{
    /// <summary>构造一个带参数的上下文，减少每个测试的样板。</summary>
    private static ToolContext Context(string text, params (string Key, string Value)[] parameters)
    {
        var dict = new Dictionary<string, string>(parameters.Length);
        foreach (var (key, value) in parameters)
            dict[key] = value;

        return new ToolContext
        {
            Input = new TextInput(text),
            Parameters = dict
        };
    }

    private static async Task<ToolResult> RunAsync(string text, params (string Key, string Value)[] parameters) =>
        await new JsonFormatTool().ExecuteAsync(Context(text, parameters), CancellationToken.None);

    [Fact]
    public async Task 格式化_输出带缩进且保留中文()
    {
        var result = await RunAsync("""{"名字":"麻雀","版本":1}""", ("mode", "格式化"), ("indent", "2"));

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<TextPayload>().Text;
        text.ShouldContain("\"名字\": \"麻雀\"");
        text.ShouldContain("\n");
    }

    [Fact]
    public async Task 格式化_缩进为四空格()
    {
        var result = await RunAsync("""{"a":1}""", ("mode", "格式化"), ("indent", "4"));

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldContain("    \"a\": 1");
    }

    [Fact]
    public async Task 压缩_输出无空白()
    {
        var result = await RunAsync(
            """
            {
              "a": 1,
              "b": [1, 2]
            }
            """,
            ("mode", "压缩"));

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("""{"a":1,"b":[1,2]}""");
    }

    [Fact]
    public async Task 校验_合法输入返回合法()
    {
        var result = await RunAsync("""{"a":1}""", ("mode", "校验"));

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("JSON 合法");
    }

    [Fact]
    public async Task 非法输入_返回InvalidInput且带行列号()
    {
        var result = await RunAsync("""{"a": }""");

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
        result.Error.Message.ShouldContain("第 1 行");
    }

    [Fact]
    public async Task 空输入_返回InvalidInput而不是抛异常()
    {
        var result = await RunAsync("   ");

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 非文本输入_返回InvalidInput而不是抛异常()
    {
        var result = await new JsonFormatTool().ExecuteAsync(
            new ToolContext { Input = EmptyInput.Instance },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 未传参数_使用默认值格式化()
    {
        var result = await RunAsync("""{"a":1}""");

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldContain("\n  \"a\": 1");
    }

    [Fact]
    public async Task 描述符_与plugin清单中的工具ID一致()
    {
        var descriptor = new JsonFormatTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.json.format");
        descriptor.Accepts.ShouldBe(InputShape.Text);
        descriptor.Produces.ShouldBe(OutputShape.Text);
        descriptor.Requires.ShouldBe(PlatformCapability.None);
        descriptor.Keywords.Length.ShouldBeGreaterThanOrEqualTo(3);
    }
}
