using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 最小文件索引：枚举临时目录，验证文件名过滤、前缀优先、目录命中与排除规则。
/// 不碰真实磁盘、不启动窗口。
/// </summary>
public sealed class SimpleFileSearchServiceTests : IDisposable
{
    private readonly string _root;

    public SimpleFileSearchServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "sparrow-search-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);
    }

    private string WriteFile(string relative)
    {
        var path = Path.Combine(_root, relative);
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, "x");
        return path;
    }

    private SimpleFileSearchService BuildService(int limit = 100)
    {
        var service = new SimpleFileSearchService(
            new NullLog(),
            [_root],
            maxEntries: limit);

        service.BuildIndex(CancellationToken.None);
        return service;
    }

    [Fact]
    public async Task 按文件名搜索_前缀结果排在包含结果前面()
    {
        WriteFile("report.txt");
        WriteFile("my-report-2026.txt");
        WriteFile("notes.md");

        using var service = BuildService();

        var results = await service.SearchAsync("report", 10, TestContext.Current.CancellationToken);

        results[0].Name.ShouldBe("report.txt");
        results.Any(item => item.Name == "my-report-2026.txt").ShouldBeTrue();
        results.Any(item => item.Name == "notes.md").ShouldBeFalse();
    }

    [Fact]
    public async Task 子目录里的文件也能搜到()
    {
        var deep = WriteFile(@"sub\deep.txt");

        using var service = BuildService();

        var results = await service.SearchAsync("deep", 10, TestContext.Current.CancellationToken);

        results.ShouldContain(item => item.FullPath == deep);
    }

    [Fact]
    public async Task 文件夹本身也能搜到()
    {
        Directory.CreateDirectory(Path.Combine(_root, "subfolder"));
        WriteFile(@"subfolder\inner.txt");

        using var service = BuildService();

        var results = await service.SearchAsync("subfolder", 10, TestContext.Current.CancellationToken);

        results.ShouldContain(item => item.Name == "subfolder" && item.IsDirectory);
    }

    [Fact]
    public async Task 空查询不返回结果()
    {
        WriteFile("anything.txt");

        using var service = BuildService();

        (await service.SearchAsync("", 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await service.SearchAsync("   ", 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task 排除目录不参与索引()
    {
        WriteFile(@"node_modules\hidden.js");

        using var service = BuildService();

        (await service.SearchAsync("hidden", 10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task 结果数量不超过上限()
    {
        for (var i = 0; i < 5; i++)
            WriteFile($"file{i}.txt");

        using var service = BuildService();

        var results = await service.SearchAsync("file", 2, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task 已取消的搜索_直接返回取消()
    {
        WriteFile("file.txt");

        using var service = BuildService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => service.SearchAsync("file", 10, cancellation.Token));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结论
        }
    }

    private sealed class NullLog : IAppLog
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
