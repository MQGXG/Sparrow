using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 批量图片识别：文件清单校验、逐张识别、失败不中断、结果按文件分节。
/// 用假 runner 替代真实模型，测试不依赖 PaddleOCR。
/// </summary>
public sealed class OcrBatchToolTests : IDisposable
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

        public List<int> ReceivedSizes { get; } = [];

        public Task<string> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
        {
            ReceivedSizes.Add(image.Length);
            return Task.FromResult(_outputs.Count > 0 ? _outputs.Dequeue() : """{"text":"","regions":[]}""");
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

    private readonly string _root;
    private readonly string _workerDirectory;

    public OcrBatchToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        _workerDirectory = Path.Combine(_root, "worker");
        Directory.CreateDirectory(_workerDirectory);

        // 工具只校验 worker 是否存在；真实执行交给假 runner
        File.WriteAllBytes(Path.Combine(_workerDirectory, "Sparrow.Ocr.Worker.exe"), [0]);
    }

    private ToolHost CreateHost() => new("sparrow.plugin.ocr", _root, new NullLog());

    private string CreateImage(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private static string Result(string text) =>
        $$"""{"text":"{{text}}","regions":[]}""";

    [Fact]
    public async Task 非文件输入_返回InvalidInput()
    {
        var result = await new OcrBatchTool(new FakeRunner([])).ExecuteAsync(
            new ToolContext { Input = new TextInput("a"), Host = CreateHost() },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 空文件清单_返回InvalidInput()
    {
        var result = await new OcrBatchTool(new FakeRunner([])).ExecuteAsync(
            new ToolContext
            {
                Input = new FileSetInput([]),
                Host = CreateHost(),
                Files = new ScopedFileAccess([])
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 没有宿主_返回Failed()
    {
        var path = CreateImage("a.png");

        var result = await new OcrBatchTool(new FakeRunner([])).ExecuteAsync(
            new ToolContext { Input = new FileSetInput([path]) },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
    }

    [Fact]
    public async Task 宿主没有文件能力_返回Failed()
    {
        var path = CreateImage("a.png");

        var result = await new OcrBatchTool(new FakeRunner([])).ExecuteAsync(
            new ToolContext { Input = new FileSetInput([path]), Host = CreateHost() },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("文件");
    }

    [Fact]
    public async Task 多个图片_复用同一个会话并按文件分节()
    {
        var first = CreateImage("a.png");
        var second = CreateImage("b.jpg");

        var runner = new FakeRunner([Result("第一张"), Result("第二张")]);
        var progress = new CollectingProgress();

        var result = await new OcrBatchTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new FileSetInput([first, second]),
                Host = CreateHost(),
                Files = new ScopedFileAccess([first, second]),
                Progress = progress
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
        text.ShouldContain("## a.png");
        text.ShouldContain("第一张");
        text.ShouldContain("## b.jpg");
        text.ShouldContain("第二张");

        // 模型只加载一次：批量不能每张图重启 worker
        runner.Sessions.ShouldBe(1);
        runner.Session.ReceivedSizes.Count.ShouldBe(2);
        progress.Reports.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task 单张失败_不影响其他文件()
    {
        var first = CreateImage("a.png");
        var second = CreateImage("b.png");

        var runner = new FakeRunner(["""{"error":"无法解码图片"}""", Result("第二张")]);

        var result = await new OcrBatchTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new FileSetInput([first, second]),
                Host = CreateHost(),
                Files = new ScopedFileAccess([first, second])
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
        text.ShouldContain("第二张");
        text.ShouldContain("未识别：1 个文件");
        text.ShouldContain("- a.png");
    }

    [Fact]
    public async Task 非图片文件_跳过并在结果里说明()
    {
        var image = CreateImage("a.png");
        var document = Path.Combine(_root, "readme.pdf");
        File.WriteAllText(document, "x");

        var runner = new FakeRunner([Result("文字")]);

        var result = await new OcrBatchTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new FileSetInput([image, document]),
                Host = CreateHost(),
                Files = new ScopedFileAccess([image, document])
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldContain("readme.pdf");

        // 只有图片进入 worker
        runner.Session.ReceivedSizes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task 全部不是图片_返回Failed并说明支持的格式()
    {
        var document = Path.Combine(_root, "readme.pdf");
        File.WriteAllText(document, "x");

        var result = await new OcrBatchTool(new FakeRunner([])).ExecuteAsync(
            new ToolContext
            {
                Input = new FileSetInput([document]),
                Host = CreateHost(),
                Files = new ScopedFileAccess([document])
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("没有识别到文字");
        result.Error.Detail!.ShouldContain("png");
    }

    [Fact]
    public void 描述符_声明文件输入与Ocr能力()
    {
        var descriptor = new OcrBatchTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.ocr.batch");
        descriptor.Accepts.ShouldBe(InputShape.Files);
        descriptor.Produces.ShouldBe(OutputShape.Text);
        descriptor.Requires.ShouldBe(PlatformCapability.Ocr);
        descriptor.Parameters.ShouldContain(p => p.Key == "withFileName");
    }

    [Fact]
    public void 关闭文件名标题_只输出正文()
    {
        var text = OcrBatchTool.ComposeBatchText(
            [("a.png", "一"), ("b.png", "二")],
            withFileName: false);

        text.ShouldNotContain("##");
        text.ShouldContain("一");
        text.ShouldContain("二");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录留给系统清理
        }
    }
}
