using System.Runtime.CompilerServices;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

public sealed partial class OcrImageToolTests
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

        public bool Disposed { get; private set; }

        public Task<string> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_outputs.Count > 0 ? _outputs.Dequeue() : Result(string.Empty));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRunner : IOcrWorkerRunner
    {
        private readonly FakeSession _session;

        public FakeRunner(IEnumerable<string> outputs) => _session = new FakeSession(outputs);

        public int Sessions { get; private set; }

        public string? LastMode { get; private set; }

        public FakeSession Session => _session;

        public Task<IOcrWorkerSession> StartAsync(
            string workerPath,
            string language,
            string mode,
            CancellationToken cancellationToken)
        {
            Sessions++;
            LastMode = mode;
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

        public Exception? Throw { get; set; }

        /// <summary>非空时按流式返回这些累积快照（测试「一个字一个字出来」）。</summary>
        public List<string> StreamSnapshots { get; } = [];

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (Throw is not null)
                throw Throw;

            return Task.FromResult(new AiResponse(Reply));
        }

        public async IAsyncEnumerable<string> StreamAsync(
            AiRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (StreamSnapshots.Count == 0)
            {
                var response = await CompleteAsync(request, cancellationToken);
                yield return response.Text;
                yield break;
            }

            LastRequest = request;

            foreach (var snapshot in StreamSnapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return snapshot;
            }
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
    public async Task 宿主卸载_会释放OCR热池()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner([Result("hello")]);
            using var host = CreateHost(root);
            var tool = new OcrImageTool(runner);

            var result = await tool.ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = host
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            runner.Session.Disposed.ShouldBeFalse();

            host.Dispose();

            // 清理是异步的；假 session 通常同步完成，留一点时间给实现替换
            for (var i = 0; i < 50 && !runner.Session.Disposed; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            runner.Session.Disposed.ShouldBeTrue();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
