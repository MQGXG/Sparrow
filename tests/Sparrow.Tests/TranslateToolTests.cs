using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Translate;
using Xunit;

namespace Sparrow.Tests;

/// <summary>翻译工具只依赖 IAiClient，测试里不碰网络。</summary>
public sealed class TranslateToolTests
{
    private sealed class FakeAi : IAiClient
    {
        public bool IsAvailable { get; set; } = true;

        public string? ModelName => "test-model";

        public AiRequest? LastRequest { get; private set; }

        public int Calls { get; private set; }

        public Func<AiRequest, string>? Reply { get; set; }

        public Exception? Throw { get; set; }

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Calls++;

            if (Throw is not null)
                throw Throw;

            return Task.FromResult(new AiResponse(Reply?.Invoke(request) ?? "translated"));
        }
    }

    private sealed class FakeSettings : IToolSettings
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string? value)
        {
            if (value is null)
                _values.Remove(key);
            else
                _values[key] = value;
        }
    }

    private sealed class FakeHost : IToolHost
    {
        public FakeHost(IAiClient ai) => Ai = ai;

        public ILogger Logger => NullLogger.Instance;

        public IToolSettings Settings { get; } = new FakeSettings();

        public IHttpClientAccess Http { get; } = new NoHttp();

        public IAiClient Ai { get; }

        public string PluginDirectory => AppContext.BaseDirectory;

        public IClipboardHistory? ClipboardHistory { get; init; }

        private sealed class NoHttp : IHttpClientAccess
        {
            public bool IsAvailable => false;

            public Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }
    }

    private static ToolContext Context(FakeAi ai, string text, IReadOnlyDictionary<string, string>? parameters = null) =>
        new()
        {
            Input = new TextInput(text),
            Host = new FakeHost(ai),
            Parameters = parameters ?? new Dictionary<string, string>()
        };

    [Fact]
    public async Task 翻译_调用模型并返回译文()
    {
        var ai = new FakeAi();
        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, "你好，世界", new Dictionary<string, string> { ["to"] = "English" }),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("translated");

        ai.LastRequest.ShouldNotBeNull();
        ai.LastRequest!.System.ShouldNotBeNull().ShouldContain("English");
        ai.LastRequest.Messages.ShouldHaveSingleItem().Text.ShouldBe("你好，世界");
    }

    [Fact]
    public async Task 未配置模型_返回Unsupported()
    {
        var ai = new FakeAi { IsAvailable = false };

        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, "hello"),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.PlatformUnsupported);
        ai.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task 长文本_分块调用并按顺序拼接()
    {
        var ai = new FakeAi { Reply = request => $"<{request.Messages[0].Text.Length}>" };
        var text = string.Join("\n", Enumerable.Repeat(new string('你', 500), 10));

        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, text),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        ai.Calls.ShouldBeGreaterThan(1);
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldContain("<");
    }

    [Fact]
    public async Task 模型返回成对引号_被去掉()
    {
        var ai = new FakeAi { Reply = _ => "“Hello”" };

        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, "你好"),
            CancellationToken.None);

        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("Hello");
    }

    [Fact]
    public async Task 模型超时_返回Failed()
    {
        var ai = new FakeAi
        {
            Throw = new AiException(AiErrorKind.Timeout, "模型请求超时")
        };

        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, "hello"),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
    }

    [Fact]
    public void 描述符_声明文本输入与AI能力()
    {
        var descriptor = new TranslateTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.text.translate");
        descriptor.Accepts.ShouldBe(InputShape.Text);
        descriptor.Produces.ShouldBe(OutputShape.Text);
        descriptor.Requires.ShouldBe(PlatformCapability.Ai);
    }
}
