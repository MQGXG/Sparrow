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

    private static ToolHost CreateHost(string pluginDirectory) =>
        new("sparrow.plugin.ocr", pluginDirectory, new NullLog());

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
    public async Task 选了翻译但模型不可用_结果保持原文且不报错()
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

            // 没配模型时不能凭空造出译文段
            text.ShouldNotContain("译文");
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
        text.ShouldContain("- a.png");
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
            [("a.png", "一"), ("b.png", ""), ("c.png", "三")]);

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
