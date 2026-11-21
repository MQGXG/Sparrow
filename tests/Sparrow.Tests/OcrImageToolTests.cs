using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

public sealed class OcrImageToolTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeSession : IOcrWorkerSession
    {
        private readonly Queue<string> _outputs;

        public FakeSession(IEnumerable<string> outputs) => _outputs = new Queue<string>(outputs);

        public int Calls { get; private set; }

        public Task<string> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_outputs.Count > 0 ? _outputs.Dequeue() : Result(string.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRunner : IOcrWorkerRunner
    {
        private readonly FakeSession _session;

        public FakeRunner(IEnumerable<string> outputs) => _session = new FakeSession(outputs);

        public int Sessions { get; private set; }

        public FakeSession Session => _session;

        public Task<IOcrWorkerSession> StartAsync(
            string workerPath,
            string language,
            CancellationToken cancellationToken)
        {
            Sessions++;
            return Task.FromResult<IOcrWorkerSession>(_session);
        }
    }

    private sealed class CollectingProgress : IProgress<ToolProgress>
    {
        public List<ToolProgress> Reports { get; } = [];

        public void Report(ToolProgress value) => Reports.Add(value);
    }

    private static string Result(string text) =>
        $$"""{"text":"{{text}}","regions":[]}""";

    private static ImageInput Image(string name, params byte[] bytes)
    {
        byte[] data = bytes.Length == 0 ? [1, 2, 3] : bytes;
        return new ImageInput(data, ImageFormat.Png) { Name = name };
    }

    /// <summary>只校验 worker 是否存在的假宿主目录。</summary>
    private static string CreateWorkerDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "worker");
        Directory.CreateDirectory(worker);
        File.WriteAllBytes(Path.Combine(worker, "Sparrow.Ocr.Worker.exe"), [0]);
        return root;
    }

    private sealed class FakeAi : IAiClient
    {
        public bool IsAvailable { get; set; } = true;

        public string? ModelName => "fake-vl";

        public AiRequest? LastRequest { get; private set; }

        public string Reply { get; set; } = "<table><tr><td>A</td></tr></table>";

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new AiResponse(Reply));
        }
    }

    private static ToolHost CreateHost(string pluginDirectory, IAiClient? ai = null) =>
        new("sparrow.plugin.ocr", pluginDirectory, new NullLog(), ai: ai);

    private sealed class FakeModels : ILocalModels
    {
        public FakeModels(params string[] installed) =>
            Installed = new HashSet<string>(installed, StringComparer.Ordinal);

        public HashSet<string> Installed { get; }

        public IReadOnlyList<LocalModelInfo> All { get; } =
            [new("hy-mt-1.8b", "小模型", "测试", 1, 4, "测试", ["https://example.com/a.gguf"])];

        public bool IsInstalled(string modelId) => Installed.Contains(modelId);

        public Task<string?> EnsureAsync(
            string modelId,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(IsInstalled(modelId) ? "model.gguf" : null);

        public Task<bool> DeleteAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeTranslation : ILocalTranslation
    {
        private readonly string? _result;

        public FakeTranslation(string? result) => _result = result;

        public LocalTranslationRequest? LastRequest { get; private set; }

        public bool IsAvailable(string modelId) => true;

        public Task<string?> TranslateAsync(
            LocalTranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task 非图片输入_返回InvalidInput()
    {
        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext { Input = new TextInput("not an image"), Host = NullToolHost.Instance },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 空图片集_返回InvalidInput()
    {
        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([]),
                Host = NullToolHost.Instance
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 没有宿主_返回Failed而不是抛异常()
    {
        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext { Input = new ImageSetInput([Image("a.png")]) },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
    }

    [Fact]
    public async Task worker不存在_返回Failed且带上路径()
    {
        var host = new ToolHost("sparrow.plugin.ocr", Path.GetTempPath(), new NullLog());

        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png")]),
                Host = host
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("worker");
    }

    [Fact]
    public void 语言参数列出全部内置语言()
    {
        var language = new OcrImageTool().Descriptor.Parameters
            .Single(parameter => parameter.Key == "language");

        // 模型包本来就带 12 类，之前只暴露了 16 个代码，现在补齐
        language.Choices.Length.ShouldBe(24);
        language.Choices.ShouldContain("it");
        language.Choices.ShouldContain("te");

        language.ChoiceLabels.ShouldNotBeNull();
        language.ChoiceLabels!["it"].ShouldBe("意大利语（it）");
        language.ChoiceLabels["te"].ShouldBe("泰卢固语（te）");
    }

    [Fact]
    public void 描述符_声明图片集输入与Ocr能力()
    {
        var descriptor = new OcrImageTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.ocr.image");
        descriptor.Accepts.ShouldBe(InputShape.Images);
        descriptor.Produces.ShouldBe(OutputShape.Text);
        descriptor.Requires.ShouldBe(PlatformCapability.Ocr);
        descriptor.Parameters.ShouldContain(p => p.Key == "language");
        descriptor.Parameters.ShouldContain(p => p.Key == "layout");
    }

    [Fact]
    public async Task 单张图片_输出纯文本不带文件名标题()
    {
        var runner = new FakeRunner([Result("HELLO")]);

        var result = await new OcrImageTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png")]),
                Host = CreateHost(CreateWorkerDirectory())
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
        text.ShouldBe("HELLO");
        text.ShouldNotContain("##");
    }

    [Fact]
    public async Task 没配在线模型_用本地引擎翻译()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner([Result("hello world")]);
            var translation = new FakeTranslation("你好，世界");

            var host = new ToolHost(
                "sparrow.plugin.ocr",
                root,
                new NullLog(),
                localModels: new FakeModels("hy-mt-1.8b"),
                localTranslation: translation);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = host,
                    Parameters = new Dictionary<string, string> { ["translate"] = "中文" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
            text.ShouldContain("hello world");
            text.ShouldContain("译文");
            text.ShouldContain("你好，世界");

            translation.LastRequest.ShouldNotBeNull().TargetLanguage.ShouldBe("中文");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    [Fact]
    public async Task 表格模式_走视觉模型并输出HTML()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var ai = new FakeAi();
            var result = await new OcrImageTool(new FakeRunner([Result("ignored")])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("table.png")]),
                    Host = CreateHost(root, ai),
                    Parameters = new Dictionary<string, string> { ["output"] = "table-html" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldContain("<table>");

            var request = ai.LastRequest.ShouldNotBeNull();
            request.Image.Length.ShouldBeGreaterThan(0);
            request.System.ShouldNotBeNull().ShouldContain("HTML");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 表格模式_去掉模型加的代码围栏()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var ai = new FakeAi { Reply = "```html\n<table><tr><td>1</td></tr></table>\n```" };

            var result = await new OcrImageTool(new FakeRunner([Result("ignored")])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("table.png")]),
                    Host = CreateHost(root, ai),
                    Parameters = new Dictionary<string, string> { ["output"] = "table-md" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.Payload.ShouldBeOfType<OcrResultPayload>().Text
                .ShouldBe("<table><tr><td>1</td></tr></table>");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 表格模式_没有视觉模型_提示去配置()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var result = await new OcrImageTool(new FakeRunner([Result("ignored")])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("table.png")]),
                    Host = CreateHost(root),
                    Parameters = new Dictionary<string, string> { ["output"] = "table-html" }
                },
                CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.Error!.Kind.ShouldBe(ToolErrorKind.PlatformUnsupported);
            result.Error.Message.ShouldContain("视觉模型");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 选了翻译但模型不可用_保留原文并说明未翻译()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner([Result("第一张的内容")]);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = CreateHost(root),
                    // 参数键与 OcrParameters.TranslateKey 一致（那个常量是 internal）
                    Parameters = new Dictionary<string, string> { ["translate"] = "English" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue("翻译是附加能力，没配模型不能拖垮识别");

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
            text.ShouldContain("第一张的内容");

            // 不能凭空造译文，但必须告诉用户「没翻」以及去哪里配
            text.ShouldContain("未翻译");
            text.ShouldContain("设置");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    [Fact]
    public async Task 多图标题带序号和尺寸()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner([Result("甲"), Result("乙")]);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput(
                    [
                        new ImageInput(new byte[] { 1, 2, 3 }, ImageFormat.Png)
                        {
                            Name = "a.png", Width = 1200, Height = 800
                        },
                        new ImageInput(new byte[] { 4, 5, 6 }, ImageFormat.Png)
                        {
                            Name = "b.png", Width = 640, Height = 480
                        }
                    ]),
                    Host = CreateHost(root)
                },
                CancellationToken.None);

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;

            // 标题要能让用户对上是哪张图
            text.ShouldContain("1. a.png（1200×800）");
            text.ShouldContain("2. b.png（640×480）");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    [Fact]
    public async Task 多张图片_复用同一会话并按文件名分节()
    {
        var runner = new FakeRunner([Result("第一张"), Result("第二张")]);
        var progress = new CollectingProgress();

        var result = await new OcrImageTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png"), Image("b.jpg")]),
                Host = CreateHost(CreateWorkerDirectory()),
                Progress = progress
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
        text.ShouldContain("1. a.png");
        text.ShouldContain("第一张");
        text.ShouldContain("2. b.jpg");
        text.ShouldContain("第二张");

        // 模型只加载一次
        runner.Sessions.ShouldBe(1);
        runner.Session.Calls.ShouldBe(2);
        progress.Reports.ShouldNotBeEmpty();
        progress.Reports.Last().Percent.ShouldBe(1);
    }

    [Fact]
    public async Task 单张失败_不影响其他图片()
    {
        var runner = new FakeRunner(["""{"error":"无法解码图片"}""", Result("第二张")]);

        var result = await new OcrImageTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png"), Image("b.png")]),
                Host = CreateHost(CreateWorkerDirectory())
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
        text.ShouldContain("第二张");
        text.ShouldContain("未识别：1 个文件");
        text.ShouldContain("- 1. a.png");
    }

    [Fact]
    public async Task 全部失败_返回Failed()
    {
        var runner = new FakeRunner(["""{"error":"坏图"}"""]);

        var result = await new OcrImageTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png")]),
                Host = CreateHost(CreateWorkerDirectory())
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("没有识别到文字");
    }

    [Fact]
    public void 多图拼接_关闭失败项只留正文()
    {
        var text = OcrImageTool.ComposeBatchText(
        [
            new TextSection { Title = "a.png", Text = "一" },
            new TextSection { Title = "b.png", Status = TextSectionStatus.Failed },
            new TextSection { Title = "c.png", Text = "三" }
        ]);

        text.ShouldContain("## a.png");
        text.ShouldContain("## c.png");
        text.ShouldContain("未识别：1 个文件");
        text.ShouldContain("- b.png");
    }

    /// <summary>
    /// 真实 IPC：宿主 → 子进程 → 宿主。
    /// 依赖 OCR 插件构建目录里的 worker，路径从仓库根算，不依赖测试输出目录。
    /// </summary>
    [Fact]
    public async Task 真实worker_单张图片能识别()
    {
        var repoRoot = FindRepoRoot();
        var pluginDirectory = Path.Combine(
            repoRoot, "plugins", "sparrow.plugin.ocr", "bin", "Debug", "net10.0");

        Directory.Exists(pluginDirectory).ShouldBeTrue(
            $"OCR 插件没有构建：{pluginDirectory}");

        var host = new ToolHost("sparrow.plugin.ocr", pluginDirectory, new NullLog());
        var png = CreatePng(400, 100);

        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput(
                [
                    new ImageInput(png, ImageFormat.Png) { Name = "hello.png" }
                ]),
                Host = host
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldContain("HELL");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Sparrow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("找不到仓库根目录（Sparrow.slnx）");
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);

        // 写一行文字，确保测的是「识别」而不是「处理空白图」
        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 36);
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
        canvas.DrawText("HELLO", 20, height - 30, font, paint);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
