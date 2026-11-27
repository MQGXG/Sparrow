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
    public void 按文件名搜索_前缀结果排在包含结果前面()
    {
        WriteFile("report.txt");
        WriteFile("my-report-2026.txt");
        WriteFile("notes.md");

        using var service = BuildService();

        var results = service.Search("report", 10);

        results[0].Name.ShouldBe("report.txt");
        results.Any(item => item.Name == "my-report-2026.txt").ShouldBeTrue();
        results.Any(item => item.Name == "notes.md").ShouldBeFalse();
    }

    [Fact]
    public void 子目录里的文件也能搜到()
    {
        var deep = WriteFile(@"sub\deep.txt");

        using var service = BuildService();

        var results = service.Search("deep", 10);

        results.ShouldContain(item => item.FullPath == deep);
    }

    [Fact]
    public void 文件夹本身也能搜到()
    {
        Directory.CreateDirectory(Path.Combine(_root, "subfolder"));
        WriteFile(@"subfolder\inner.txt");

        using var service = BuildService();

        var results = service.Search("subfolder", 10);

        results.ShouldContain(item => item.Name == "subfolder" && item.IsDirectory);
    }

    [Fact]
    public void 空查询不返回结果()
    {
        WriteFile("anything.txt");

        using var service = BuildService();

        service.Search("", 10).ShouldBeEmpty();
        service.Search("   ", 10).ShouldBeEmpty();
    }

    [Fact]
    public void 排除目录不参与索引()
    {
        WriteFile(@"node_modules\hidden.js");

        using var service = BuildService();

        service.Search("hidden", 10).ShouldBeEmpty();
    }

    [Fact]
    public void 结果数量不超过上限()
    {
        for (var i = 0; i < 5; i++)
            WriteFile($"file{i}.txt");

        using var service = BuildService();

        service.Search("file", 2).Count.ShouldBe(2);
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
