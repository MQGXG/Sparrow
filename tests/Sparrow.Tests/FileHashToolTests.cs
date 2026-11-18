using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.Plugin.Files.Hash;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 文件哈希：算法结果正确、多个文件按顺序、单个失败不中断。
/// 用真实临时文件，不 mock 哈希——算法参数写错必须能被测试发现。
/// </summary>
public sealed class FileHashToolTests : IDisposable
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class CollectingProgress : IProgress<ToolProgress>
    {
        public List<ToolProgress> Reports { get; } = [];

        public void Report(ToolProgress value) => Reports.Add(value);
    }

    private const string HelloSha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
    private const string HelloMd5 = "5d41402abc4b2a76b9719d911017c592";
    private const string HelloSha1 = "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly string _root;

    public FileHashToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ToolContext CreateContext(
        IReadOnlyList<string> paths,
        IProgress<ToolProgress>? progress = null,
        string? algorithm = null)
    {
        var parameters = algorithm is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["algorithm"] = algorithm };

        return new ToolContext
        {
            Input = new FileSetInput(paths),
            Host = new ToolHost("sparrow.plugin.files-hash", Path.GetTempPath(), new NullLog()),
            Files = new ScopedFileAccess(paths),
            Parameters = parameters,
            Progress = progress
        };
    }

    [Fact]
    public async Task 单个文件_默认SHA256()
    {
        var path = CreateFile("hello.txt", "hello");

        var result = await new FileHashTool().ExecuteAsync(CreateContext([path]), CancellationToken.None);

        result.Success.ShouldBeTrue();

        var table = result.Payload.ShouldBeOfType<TablePayload>();
        table.Columns[2].ShouldContain("SHA256");
        table.Rows.ShouldHaveSingleItem()[2].ShouldBe(HelloSha256);
    }

    [Theory]
    [InlineData("MD5", HelloMd5)]
    [InlineData("SHA1", HelloSha1)]
    [InlineData("SHA256", HelloSha256)]
    public async Task 算法参数_决定输出(string algorithm, string expected)
    {
        var path = CreateFile("hello.txt", "hello");

        var result = await new FileHashTool().ExecuteAsync(
            CreateContext([path], algorithm: algorithm),
            CancellationToken.None);

        result.Payload.ShouldBeOfType<TablePayload>()
            .Rows.Single()[2].ShouldBe(expected);
    }

    [Fact]
    public async Task 空文件_也有哈希()
    {
        var path = CreateFile("empty.bin", string.Empty);

        var result = await new FileHashTool().ExecuteAsync(CreateContext([path]), CancellationToken.None);

        result.Payload.ShouldBeOfType<TablePayload>()
            .Rows.Single()[2].ShouldBe(EmptySha256);
    }

    [Fact]
    public async Task 多个文件_保持选择顺序并上报进度()
    {
        var first = CreateFile("a.txt", "hello");
        var second = CreateFile("b.txt", "hello");
        var progress = new CollectingProgress();

        var result = await new FileHashTool().ExecuteAsync(
            CreateContext([first, second], progress),
            CancellationToken.None);

        var table = result.Payload.ShouldBeOfType<TablePayload>();
        table.Rows.Count.ShouldBe(2);
        table.Rows[0][0].ShouldBe("a.txt");
        table.Rows[1][0].ShouldBe("b.txt");
        table.Rows[0][1].ShouldBe("5 B");

        progress.Reports.ShouldNotBeEmpty();
        progress.Reports.Last().Percent.ShouldBe(1);
    }

    [Fact]
    public async Task 单个文件读不到_标记为无法读取且不影响其他文件()
    {
        var readable = CreateFile("a.txt", "hello");
        var missing = Path.Combine(_root, "missing.txt");

        var result = await new FileHashTool().ExecuteAsync(
            CreateContext([missing, readable]),
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var table = result.Payload.ShouldBeOfType<TablePayload>();
        table.Rows[0][2].ShouldBe("无法读取");
        table.Rows[1][2].ShouldBe(HelloSha256);
    }

    [Fact]
    public async Task 全部读不到_返回Failed()
    {
        var missing = Path.Combine(_root, "missing.txt");

        var result = await new FileHashTool().ExecuteAsync(
            CreateContext([missing]),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("没有可读取的文件");
        result.Error.Detail!.ShouldContain("权限");
    }

    [Fact]
    public async Task 非文件输入_返回InvalidInput()
    {
        var result = await new FileHashTool().ExecuteAsync(
            new ToolContext { Input = new TextInput("abc") },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 空文件清单_返回InvalidInput()
    {
        var result = await new FileHashTool().ExecuteAsync(
            CreateContext([]),
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 宿主没有文件能力_返回Failed()
    {
        var path = CreateFile("a.txt", "hello");

        var result = await new FileHashTool().ExecuteAsync(
            new ToolContext
            {
                Input = new FileSetInput([path]),
                Host = new ToolHost("sparrow.plugin.files-hash", Path.GetTempPath(), new NullLog())
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("文件");
    }

    [Fact]
    public void 描述符_声明文件输入与表格输出()
    {
        var descriptor = new FileHashTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.files.hash");
        descriptor.Accepts.ShouldBe(InputShape.Files);
        descriptor.Produces.ShouldBe(OutputShape.Table);
        descriptor.Parameters.ShouldContain(parameter => parameter.Key == "algorithm");
    }

    [Fact]
    public async Task 未知算法_回退SHA256而不是失败()
    {
        var path = CreateFile("hello.txt", "hello");

        var result = await new FileHashTool().ExecuteAsync(
            CreateContext([path], algorithm: "CRC32"),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TablePayload>()
            .Rows.Single()[2].ShouldBe(HelloSha256);
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
