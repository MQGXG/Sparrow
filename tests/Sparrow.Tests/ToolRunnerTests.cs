using Shouldly;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 工具统一执行入口：超时、取消、异常与正常结果都要保持原来的语义。
/// </summary>
public sealed class ToolRunnerTests
{
    private sealed class FakeTool(Func<CancellationToken, Task<ToolResult>> body) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "sparrow.test.runner",
            Name = "测试工具",
            Category = "test"
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            body(cancellationToken);
    }

    private static ToolContext Context() => new() { Input = EmptyInput.Instance };

    [Fact]
    public async Task 成功_原样返回结果()
    {
        var tool = new FakeTool(_ => Task.FromResult(ToolResult.Ok(new TextPayload("ok"))));

        var result = await ToolRunner.ExecuteAsync(tool, Context(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("ok");
    }

    [Fact]
    public async Task 同步实现_也能正常返回()
    {
        var tool = new FakeTool(_ =>
        {
            // 模拟 JSON / 图片这类同步 CPU 工作
            Thread.Sleep(10);
            return Task.FromResult(ToolResult.Ok(new TextPayload("sync")));
        });

        var result = await ToolRunner.ExecuteAsync(tool, Context(), TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("sync");
    }

    [Fact]
    public async Task 超时_返回Timeout而不是一直等待()
    {
        var tool = new FakeTool(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ToolResult.Ok(new TextPayload("never"));
        });

        var result = await ToolRunner.ExecuteAsync(
            tool,
            Context(),
            TestContext.Current.CancellationToken,
            TimeSpan.FromMilliseconds(100));

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Timeout);
        result.Error.Message.ShouldContain("超时");
    }

    [Fact]
    public async Task 已取消_返回Cancelled()
    {
        var tool = new FakeTool(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ToolResult.Ok(new TextPayload("never"));
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await ToolRunner.ExecuteAsync(tool, Context(), cts.Token);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Cancelled);
    }

    [Fact]
    public async Task 插件异常_仍交给调用方处理()
    {
        var tool = new FakeTool(_ => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => ToolRunner.ExecuteAsync(tool, Context(), TestContext.Current.CancellationToken));
    }
}
