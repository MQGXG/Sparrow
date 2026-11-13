using System.Net;
using System.Text;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

public sealed class AiClientTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static string TempKeyPath() => Path.Combine(
        Path.GetTempPath(),
        "sparrow-tests",
        Guid.NewGuid().ToString("N"),
        "ai-key.dat");

    private static AiClient CreateClient(AppSettings settings)
    {
        var log = new NullLog();
        var client = new AiClient(new AiSecretStore(TempKeyPath(), log), log);
        client.Configure(settings);
        return client;
    }

    [Fact]
    public void 本地端点_不需要密钥也可用()
    {
        var client = CreateClient(new AppSettings
        {
            AiEnabled = true,
            AiProvider = "ollama",
            AiBaseUrl = "http://localhost:11434/v1",
            AiModel = "qwen2.5vl:7b"
        });

        client.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void 云端端点_没有密钥不可用()
    {
        var client = CreateClient(new AppSettings
        {
            AiEnabled = true,
            AiProvider = "openai",
            AiBaseUrl = "https://api.openai.com/v1",
            AiModel = "gpt-4o-mini"
        });

        client.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public void 未启用时_即使配置完整也不可用()
    {
        var client = CreateClient(new AppSettings
        {
            AiEnabled = false,
            AiBaseUrl = "http://localhost:11434/v1",
            AiModel = "qwen"
        });

        client.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public void 密钥_加密保存后可读回并清除()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = TempKeyPath();
        var store = new AiSecretStore(path, new NullLog());

        store.Save("sk-sparrow-test").ShouldBeTrue();
        store.Load().ShouldBe("sk-sparrow-test");

        // 文件里不能出现明文
        System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path))
            .ShouldNotContain("sk-sparrow-test");

        store.Save(null).ShouldBeTrue();
        store.Load().ShouldBeNull();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public HttpResponseMessage Response { get; set; } = Json(
            """{"choices":[{"message":{"content":"pong"}}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""");

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response;
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task 调用_发送OpenAI兼容请求并解析用量()
    {
        var log = new NullLog();
        var path = TempKeyPath();
        var secrets = new AiSecretStore(path, log);
        secrets.Save("sk-test-key");

        var handler = new FakeHandler();
        var client = new AiClient(secrets, log, handler);
        client.Configure(new AppSettings
        {
            AiEnabled = true,
            AiProvider = "openai",
            AiBaseUrl = "https://example.com/v1",
            AiModel = "gpt-test"
        });

        var response = await client.CompleteAsync(
            new AiRequest
            {
                System = "你是翻译",
                Messages = [new AiMessage(AiRole.User, "你好")],
                MaxTokens = 64
            },
            CancellationToken.None);

        response.Text.ShouldBe("pong");
        response.PromptTokens.ShouldBe(3);
        response.CompletionTokens.ShouldBe(1);

        handler.Request!.RequestUri!.ToString().ShouldBe("https://example.com/v1/chat/completions");
        handler.Request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.Request.Headers.Authorization.Parameter.ShouldBe("sk-test-key");
        handler.Body.ShouldNotBeNull().ShouldContain("\"model\":\"gpt-test\"");
        handler.Body.ShouldContain("你是翻译");
        handler.Body.ShouldContain("你好");
    }

    [Fact]
    public async Task 图片请求_编码为dataURL()
    {
        var log = new NullLog();
        var secrets = new AiSecretStore(TempKeyPath(), log);
        secrets.Save("sk-test-key");

        var handler = new FakeHandler();
        var client = new AiClient(secrets, log, handler);
        client.Configure(new AppSettings
        {
            AiEnabled = true,
            AiBaseUrl = "https://example.com/v1",
            AiModel = "vision-test"
        });

        await client.CompleteAsync(
            new AiRequest
            {
                Messages = [new AiMessage(AiRole.User, "看图")],
                Image = new byte[] { 1, 2, 3 },
                ImageMimeType = "image/png"
            },
            CancellationToken.None);

        handler.Body.ShouldNotBeNull().ShouldContain("data:image/png;base64,AQID");
        handler.Body.ShouldContain("image_url");
    }

    [Fact]
    public async Task 未授权_映射为Unauthorized()
    {
        var log = new NullLog();
        var secrets = new AiSecretStore(TempKeyPath(), log);
        secrets.Save("bad-key");

        var handler = new FakeHandler
        {
            Response = FakeHandler.Json("""{"error":"invalid key"}""", HttpStatusCode.Unauthorized)
        };
        var client = new AiClient(secrets, log, handler);
        client.Configure(new AppSettings
        {
            AiEnabled = true,
            AiBaseUrl = "https://example.com/v1",
            AiModel = "gpt-test"
        });

        var error = await Should.ThrowAsync<AiException>(() =>
            client.CompleteAsync(
                new AiRequest { Messages = [new AiMessage(AiRole.User, "hi")] },
                CancellationToken.None));

        error.Kind.ShouldBe(AiErrorKind.Unauthorized);
    }
}
