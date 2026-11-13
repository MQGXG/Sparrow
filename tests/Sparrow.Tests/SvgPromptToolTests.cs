using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Svg.Prompt;
using Xunit;

namespace Sparrow.Tests;

public sealed class SvgPromptToolTests
{
    private sealed class FakeAi : IAiClient
    {
        public bool IsAvailable { get; set; } = true;

        public string? ModelName => "fake";

        public AiRequest? LastRequest { get; private set; }

        public string Reply { get; set; } = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 512 512\"><rect width=\"512\" height=\"512\" fill=\"red\"/></svg>";

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new AiResponse(Reply));
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

        private sealed class NoHttp : IHttpClientAccess
        {
            public bool IsAvailable => false;

            public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new NotSupportedException();
        }

        private sealed class FakeSettings : IToolSettings
        {
            private readonly Dictionary<string, string> _values = new();

            public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

            public void Set(string key, string? value)
            {
                if (value is null)
                    _values.Remove(key);
                else
                    _values[key] = value;
            }
        }
    }

    private static ToolContext Context(FakeAi ai, string prompt) => new()
    {
        Input = new TextInput(prompt),
        Host = new FakeHost(ai)
    };

    [Fact]
    public async Task 模型返回SVG_原样作为SvgPayload()
    {
        var ai = new FakeAi();

        var result = await new SvgPromptTool().ExecuteAsync(Context(ai, "画一个红色方块"), CancellationToken.None);

        result.Success.ShouldBeTrue();
        var svg = result.Payload.ShouldBeOfType<SvgPayload>();
        svg.Svg.ShouldContain("<svg");
        svg.Width.ShouldBe(512);
        ai.LastRequest!.System.ShouldNotBeNull().ShouldContain("512");
    }

    [Fact]
    public async Task Markdown围栏包裹_也能截出SVG()
    {
        var ai = new FakeAi
        {
            Reply = "```svg\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"><circle cx=\"4\" cy=\"4\" r=\"4\"/></svg>\n```"
        };

        var result = await new SvgPromptTool().ExecuteAsync(Context(ai, "圆"), CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<SvgPayload>().Svg.ShouldStartWith("<svg");
    }

    [Fact]
    public async Task 模型没有返回SVG_返回Failed()
    {
        var ai = new FakeAi { Reply = "抱歉，我不能画。" };

        var result = await new SvgPromptTool().ExecuteAsync(Context(ai, "画"), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
    }

    [Fact]
    public async Task 未配置模型_返回Unsupported()
    {
        var ai = new FakeAi { IsAvailable = false };

        var result = await new SvgPromptTool().ExecuteAsync(Context(ai, "画"), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.PlatformUnsupported);
    }
}
