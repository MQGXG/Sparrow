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

        /// <summary>每次调用的请求，用来断言分块与块间上下文。</summary>
        public List<AiRequest> Requests { get; } = [];

        public int Calls { get; private set; }

        public Func<AiRequest, string>? Reply { get; set; }

        public Exception? Throw { get; set; }

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests.Add(request);
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
        public FakeHost(
            IAiClient ai,
            ILocalModels? models = null,
            ILocalTranslation? translation = null)
        {
            Ai = ai;
            LocalModels = models ?? NullLocalModels.Instance;
            LocalTranslation = translation ?? NullLocalTranslation.Instance;
        }

        public ILogger Logger => NullLogger.Instance;

        public IToolSettings Settings { get; } = new FakeSettings();

        public IHttpClientAccess Http { get; } = new NoHttp();

        public IAiClient Ai { get; }

        public ILocalModels LocalModels { get; }

        public ILocalTranslation LocalTranslation { get; }

        public string PluginDirectory => AppContext.BaseDirectory;

        public IClipboardHistory? ClipboardHistory { get; init; }

        public IWindowOverlay? Windows => null;

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

    private static ToolContext Context(
        FakeAi ai,
        string text,
        IReadOnlyDictionary<string, string>? parameters,
        ILocalModels models,
        ILocalTranslation translation) =>
        new()
        {
            Input = new TextInput(text),
            Host = new FakeHost(ai, models, translation),
            Parameters = parameters ?? new Dictionary<string, string>()
        };

    private sealed class FakeLocalModels : ILocalModels
    {
        private readonly HashSet<string> _installed;

        public FakeLocalModels(params string[] installed) =>
            _installed = new HashSet<string>(installed, StringComparer.Ordinal);

        public IReadOnlyList<LocalModelInfo> All { get; } =
        [
            new("hy-mt-1.8b", "小模型", "测试", 1, 4, "测试许可", ["https://example.com/a.gguf"]),
            new("hy-mt-7b", "大模型", "测试", 2, 16, "测试许可", ["https://example.com/b.gguf"])
        ];

        public bool IsInstalled(string modelId) => _installed.Contains(modelId);

        public Task<string?> EnsureAsync(
            string modelId,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(IsInstalled(modelId) ? "model.gguf" : null);

        public Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeLocalTranslation : ILocalTranslation
    {
        public string? Result { get; set; } = "本地译文";

        public LocalTranslationRequest? LastRequest { get; private set; }

        public bool IsAvailable(string modelId) => true;

        public Task<string?> TranslateAsync(
            LocalTranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

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
    public async Task 长文本_后一块带上一块的译文作上下文()
    {
        var ai = new FakeAi { Reply = request => $"译{request.Messages[0].Text.Length}" };
        var text = string.Join("\n", Enumerable.Repeat(new string('你', 500), 10));

        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, text),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        ai.Calls.ShouldBeGreaterThan(1);

        // 第一块没有上文；第二块要带上第一块的原文末尾和译文，正文本身不混入上下文
        ai.Requests[0].System.ShouldNotBeNull().ShouldNotContain("上文已翻译");

        var second = ai.Requests[1];
        second.System.ShouldNotBeNull().ShouldContain("上文已翻译");
        second.System.ShouldContain($"译{ai.Requests[0].Messages[0].Text.Length}");  // 第一块的译文
        second.Messages.ShouldHaveSingleItem().Text.ShouldNotContain("上文已翻译");
    }

    [Fact]
    public async Task 术语表_注入系统提示词并去重()
    {
        var ai = new FakeAi();

        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, "Sparrow keeps the clipboard panel tidy.", new Dictionary<string, string>
            {
                ["glossary"] = "Sparrow=Sparrow\n剪贴板=剪贴板面板\n坏行\nSparrow=别的译法"
            }),
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var system = ai.LastRequest!.System.ShouldNotBeNull();
        system.ShouldContain("术语表");
        system.ShouldContain("Sparrow → Sparrow");
        system.ShouldContain("剪贴板 → 剪贴板面板");
        system.ShouldNotContain("坏行");
        system.ShouldNotContain("别的译法");
    }

    [Fact]
    public async Task 术语表_支持分号分隔_空表不出现()
    {
        var empty = new FakeAi();
        await new TranslateTool().ExecuteAsync(Context(empty, "hello"), CancellationToken.None);
        empty.LastRequest!.System.ShouldNotBeNull().ShouldNotContain("术语表");

        var withGlossary = new FakeAi();
        await new TranslateTool().ExecuteAsync(
            Context(withGlossary, "hello", new Dictionary<string, string> { ["glossary"] = "A=甲;B=乙" }),
            CancellationToken.None);

        var system = withGlossary.LastRequest!.System.ShouldNotBeNull();
        system.ShouldContain("A → 甲");
        system.ShouldContain("B → 乙");
    }

    [Fact]
    public void 描述符_目标语言是常用12种且都有中文名()
    {
        var descriptor = new TranslateTool().Descriptor;
        var to = ToolParameter.Find(descriptor.Parameters, "to");

        to.ShouldNotBeNull();
        to!.Choices.Length.ShouldBe(12);
        to.Default.ShouldBe("English");

        // 每种语言都要有显示名：阿拉伯语/泰语这种原生写法，用户认不出来
        foreach (var language in to.Choices)
            to.ChoiceLabels.ShouldNotBeNull().ShouldContainKey(language);

        to.Choices.ShouldContain("العربية");
        to.Choices.ShouldContain("Tiếng Việt");
    }

    [Fact]
    public async Task 离线引擎_调用宿主共享的翻译能力()
    {
        var translation = new FakeLocalTranslation();
        var models = new FakeLocalModels("hy-mt-1.8b");

        var result = await new TranslateTool().ExecuteAsync(
            Context(
                new FakeAi { IsAvailable = false },
                "Hello",
                new Dictionary<string, string>
                {
                    ["engine"] = "离线 · 1.8B",
                    ["to"] = "中文",
                    ["glossary"] = "Sparrow=Sparrow"
                },
                models,
                translation),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("本地译文");

        // 插件只描述「翻什么」，模型路径、提示词、采样都在宿主
        var request = translation.LastRequest.ShouldNotBeNull();
        request.ModelId.ShouldBe("hy-mt-1.8b");
        request.Text.ShouldBe("Hello");
        request.TargetLanguage.ShouldBe("中文");
        request.Terms.ShouldHaveSingleItem().Source.ShouldBe("Sparrow");
    }

    [Fact]
    public async Task 自动_在线不可用时选第一个已下载的本地模型()
    {
        var translation = new FakeLocalTranslation();
        var models = new FakeLocalModels("hy-mt-7b");

        var result = await new TranslateTool().ExecuteAsync(
            Context(new FakeAi { IsAvailable = false }, "Hello", null, models, translation),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        translation.LastRequest!.ModelId.ShouldBe("hy-mt-7b");
    }

    [Fact]
    public async Task 离线引擎_返回空时给出失败而不是空结果()
    {
        var translation = new FakeLocalTranslation { Result = null };
        var models = new FakeLocalModels("hy-mt-1.8b");

        var result = await new TranslateTool().ExecuteAsync(
            Context(
                new FakeAi { IsAvailable = false },
                "Hello",
                new Dictionary<string, string> { ["engine"] = "离线 · 1.8B" },
                models,
                translation),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
        result.Error.Message.ShouldContain("离线翻译失败");
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
    public void 描述符_声明文本输入且不强制要求AI()
    {
        var descriptor = new TranslateTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.text.translate");
        descriptor.Accepts.ShouldBe(InputShape.Text);
        descriptor.Produces.ShouldBe(OutputShape.Text);

        // 有离线引擎：没配 AI 也应该能用，所以不能声明 Requires.Ai（否则界面会置灰）
        descriptor.Requires.ShouldBe(PlatformCapability.None);
    }

    [Fact]
    public void 描述符_有引擎选项且默认自动()
    {
        var descriptor = new TranslateTool().Descriptor;

        var engine = ToolParameter.Find(descriptor.Parameters, "engine");
        engine.ShouldNotBeNull();
        engine!.Kind.ShouldBe(ParameterKind.Choice);
        engine.Default.ShouldBe("自动");
        engine.Choices.ShouldContain("离线 · 1.8B");
        engine.Choices.ShouldContain("离线 · 7B");
    }

    [Fact]
    public async Task 离线引擎_模型未下载时提示去设置页()
    {
        var ai = new FakeAi();

        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, "hello", new Dictionary<string, string> { ["engine"] = "离线 · 1.8B" }),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.PlatformUnsupported);
        result.Error.Message.ShouldContain("离线模型");
        result.Error.Message.ShouldContain("设置");
        ai.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task 在线引擎_未配置时不提示离线模型()
    {
        var ai = new FakeAi { IsAvailable = false };

        var result = await new TranslateTool().ExecuteAsync(
            Context(ai, "hello", new Dictionary<string, string> { ["engine"] = "在线模型" }),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.PlatformUnsupported);
        result.Error.Message.ShouldNotContain("离线模型");
    }
}
