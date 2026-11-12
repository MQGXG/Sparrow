using Shouldly;
using Sparrow.Plugin.Dev.Base64;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

public sealed class Base64ToolTests
{
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
        await new Base64Tool().ExecuteAsync(Context(text, parameters), CancellationToken.None);

    [Fact]
    public async Task 编码_中文转Base64()
    {
        var result = await RunAsync("麻雀");

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("6bq76ZuA");
    }

    [Fact]
    public async Task 解码_Base64转回中文()
    {
        var result = await RunAsync("6bq76ZuA", ("mode", "解码"));

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("麻雀");
    }

    [Fact]
    public async Task 编码_URLsafe_替换字符并去掉填充()
    {
        // "???" 的标准 Base64 是 "Pz8/"
        var result = await RunAsync("???", ("urlSafe", "true"));

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("Pz8_");
    }

    [Fact]
    public async Task 解码_URLsafe_能还原()
    {
        var result = await RunAsync("Pz8_", ("mode", "解码"), ("urlSafe", "true"));

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("???");
    }

    [Fact]
    public async Task 解码_非法输入_返回InvalidInput()
    {
        var result = await RunAsync("这不是 base64", ("mode", "解码"));

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 空输入_返回InvalidInput而不是抛异常()
    {
        var result = await RunAsync("   ");

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 非文本输入_返回InvalidInput()
    {
        var result = await new Base64Tool().ExecuteAsync(
            new ToolContext { Input = EmptyInput.Instance },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public void 描述符_与清单一致()
    {
        var descriptor = new Base64Tool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.dev.base64");
        descriptor.Accepts.ShouldBe(InputShape.Text);
        descriptor.Produces.ShouldBe(OutputShape.Text);
        descriptor.Requires.ShouldBe(PlatformCapability.None);
        descriptor.Keywords.Length.ShouldBeGreaterThanOrEqualTo(3);
    }
}
