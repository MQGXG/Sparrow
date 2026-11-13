using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sparrow.Plugin.Clipboard.History;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>剪贴板历史工具：只读宿主注入的 IClipboardHistory，不碰真实剪贴板。</summary>
public sealed class ClipboardHistoryToolTests
{
    private sealed class FakeHost : IToolHost
    {
        public FakeHost(IClipboardHistory? history = null) => ClipboardHistory = history;

        public ILogger Logger => NullLogger.Instance;

        public IToolSettings Settings { get; } = new InMemorySettings();

        public IHttpClientAccess Http { get; } = new NoHttp();

        public IAiClient Ai => new NullAi();

        public string PluginDirectory => AppContext.BaseDirectory;

        public IClipboardHistory? ClipboardHistory { get; }

        private sealed class InMemorySettings : IToolSettings
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

        private sealed class NoHttp : IHttpClientAccess
        {
            public bool IsAvailable => false;

            public Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }

        private sealed class NullAi : IAiClient
        {
            public bool IsAvailable => false;

            public string? ModelName => null;

            public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken) =>
                throw new NotSupportedException();
        }
    }

    private sealed class FakeHistory : IClipboardHistory
    {
        private readonly List<ClipboardHistoryEntry> _entries = [];
        private readonly Dictionary<string, byte[]> _images = [];

        public int Count => _entries.Count;

        public void AddText(string text)
        {
            _entries.Insert(0, new ClipboardHistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = ClipboardEntryKind.Text,
                Preview = text,
                Text = text,
                CreatedAt = DateTimeOffset.Now,
                ByteSize = text.Length
            });
        }

        public void AddImage(byte[] png)
        {
            var id = Guid.NewGuid().ToString("N");
            _images[id] = png;
            _entries.Insert(0, new ClipboardHistoryEntry
            {
                Id = id,
                Kind = ClipboardEntryKind.Image,
                Preview = "图片",
                CreatedAt = DateTimeOffset.Now,
                ByteSize = png.Length
            });
        }

        public void AddFiles(params string[] paths)
        {
            _entries.Insert(0, new ClipboardHistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = ClipboardEntryKind.Files,
                Preview = $"{paths.Length} 个文件",
                Paths = paths,
                CreatedAt = DateTimeOffset.Now,
                ByteSize = paths.Sum(p => p.Length)
            });
        }

        public IReadOnlyList<ClipboardHistoryEntry> Recent(int count) =>
            _entries.Take(Math.Max(0, count)).ToArray();

        public IReadOnlyList<ClipboardHistoryEntry> Search(string keyword, int count) =>
            _entries.Where(e => (e.Text?.Contains(keyword) ?? false) || e.Preview.Contains(keyword))
                .Take(Math.Max(0, count))
                .ToArray();

        public byte[]? LoadImage(ClipboardHistoryEntry entry) =>
            _images.TryGetValue(entry.Id, out var png) ? png : null;
    }

    private static ToolContext Context(
        FakeHost? host,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        new()
        {
            Input = new Sparrow.Plugin.Sdk.Inputs.EmptyInput(),
            Host = host,
            Parameters = parameters ?? new Dictionary<string, string>()
        };

    private static Dictionary<string, string> Params(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static readonly byte[] TestPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7, 6
    ];

    [Fact]
    public async Task 宿主未开启历史_返回Failed而不是抛异常()
    {
        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(new FakeHost()),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
        result.Error.Message.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task 宿主未注入_返回Failed而不是抛异常()
    {
        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(null),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
    }

    [Fact]
    public async Task 空历史_给出人话提示()
    {
        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(new FakeHost(new FakeHistory())),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldContain("历史为空");
    }

    [Fact]
    public async Task 浏览_输出带序号和形态标注()
    {
        var history = new FakeHistory();
        history.AddText("最早的");
        history.AddText("最新的一条");
        history.AddImage(TestPng);
        history.AddFiles("C:\\a.txt");

        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(new FakeHost(history)),
            CancellationToken.None);

        var text = result.Payload.ShouldBeOfType<TextPayload>().Text;
        text.ShouldContain("1");
        text.ShouldContain("文件");
        text.ShouldContain("图片");
        text.ShouldContain("最新的一条");
        // 最新的在最前
        text.IndexOf("文件").ShouldBeLessThan(text.IndexOf("图片"));
        text.IndexOf("图片").ShouldBeLessThan(text.IndexOf("最新的一条"));
    }

    [Fact]
    public async Task 浏览_按关键字过滤()
    {
        var history = new FakeHistory();
        history.AddText("今天买牛奶");
        history.AddText("会议纪要");

        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(new FakeHost(history), Params(("keyword", "牛奶"))),
            CancellationToken.None);

        var text = result.Payload.ShouldBeOfType<TextPayload>().Text;
        text.ShouldContain("牛奶");
        text.ShouldNotContain("会议纪要");
    }

    [Fact]
    public async Task 还原文本_返回全文()
    {
        var history = new FakeHistory();
        history.AddText("要还原的内容");
        history.AddText("更新的内容");

        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(new FakeHost(history), Params(("action", "还原"), ("index", "2"))),
            CancellationToken.None);

        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("要还原的内容");
    }

    [Fact]
    public async Task 还原图片_返回PNG字节()
    {
        var history = new FakeHistory();
        history.AddImage(TestPng);

        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(new FakeHost(history), Params(("action", "还原"), ("index", "1"))),
            CancellationToken.None);

        var image = result.Payload.ShouldBeOfType<ImagePayload>();
        image.Data.ToArray().ShouldBe(TestPng);
        image.Format.ShouldBe(ImageFormat.Png);
    }

    [Fact]
    public async Task 还原文件_返回路径列表()
    {
        var history = new FakeHistory();
        history.AddFiles("C:\\a.txt", "C:\\b.txt");

        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(new FakeHost(history), Params(("action", "还原"), ("index", "1"))),
            CancellationToken.None);

        result.Payload.ShouldBeOfType<FileSetPayload>().Paths.ShouldBe(["C:\\a.txt", "C:\\b.txt"]);
    }

    [Fact]
    public async Task 还原_序号越界返回InvalidInput()
    {
        var history = new FakeHistory();
        history.AddText("唯一一条");

        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(new FakeHost(history), Params(("action", "还原"), ("index", "5"))),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 还原_序号遵循浏览时的关键字过滤()
    {
        var history = new FakeHistory();
        history.AddText("不相关内容");
        history.AddText("牛奶清单A");
        history.AddText("牛奶清单B");

        var result = await new ClipboardHistoryTool().ExecuteAsync(
            Context(
                new FakeHost(history),
                Params(("action", "还原"), ("keyword", "牛奶"), ("index", "2"))),
            CancellationToken.None);

        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldBe("牛奶清单A");
    }

    [Fact]
    public void 描述符_声明无输入与监听能力()
    {
        var descriptor = new ClipboardHistoryTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.clipboard.history");
        descriptor.Accepts.ShouldBe(InputShape.None);
        descriptor.Requires.ShouldBe(PlatformCapability.ClipboardMonitor);
        descriptor.Keywords.Length.ShouldBeGreaterThanOrEqualTo(3);
    }
}
