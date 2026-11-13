using Shouldly;
using Sparrow.App;
using Sparrow.App.History;
using Xunit;

namespace Sparrow.Tests;

public sealed class JsonlHistoryStoreTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class TempStore : IDisposable
    {
        public TempStore()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sparrow-tests",
                Guid.NewGuid().ToString("N"),
                "history.jsonl");

            Store = new JsonlHistoryStore(Path, new NullLog());
        }

        public string Path { get; }

        public JsonlHistoryStore Store { get; }

        public void Dispose()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (directory is not null && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }

    private static HistoryEntry Entry(string output) => Entry(output, DateTimeOffset.Now);

    private static HistoryEntry Entry(string output, DateTimeOffset createdAt) => new(
        Guid.NewGuid().ToString("N"),
        "sparrow.json.format",
        "JSON 格式化",
        output,
        "input-preview",
        createdAt,
        12);

    [Fact]
    public void 添加后能按时间倒序读回()
    {
        using var temp = new TempStore();
        temp.Store.Add(Entry("first"));
        temp.Store.Add(Entry("second"));

        var recent = temp.Store.Recent(10);

        recent.Count.ShouldBe(2);
        recent[0].Output.ShouldBe("second");
        recent[1].Output.ShouldBe("first");
    }

    [Fact]
    public void Recent_只返回指定数量()
    {
        using var temp = new TempStore();
        for (var i = 0; i < 5; i++)
            temp.Store.Add(Entry($"entry-{i}"));

        temp.Store.Recent(3).Count.ShouldBe(3);
    }

    [Fact]
    public void 损坏的行_被跳过()
    {
        using var temp = new TempStore();
        temp.Store.Add(Entry("valid"));
        File.AppendAllText(temp.Path, "{ broken json" + Environment.NewLine);

        var recent = temp.Store.Recent(10);

        recent.Count.ShouldBe(1);
        recent[0].Output.ShouldBe("valid");
    }

    [Fact]
    public void Clear_清空历史()
    {
        using var temp = new TempStore();
        temp.Store.Add(Entry("x"));

        temp.Store.Clear();

        temp.Store.Recent(10).ShouldBeEmpty();
    }

    [Fact]
    public void 文件不存在_返回空列表()
    {
        using var temp = new TempStore();

        temp.Store.Recent(10).ShouldBeEmpty();
    }

    [Fact]
    public void 按天数清理_删除过期记录()
    {
        using var temp = new TempStore();
        temp.Store.Add(Entry("old", DateTimeOffset.Now.AddDays(-60)));
        temp.Store.Add(Entry("fresh", DateTimeOffset.Now));

        temp.Store.Configure(500, 30);

        var recent = temp.Store.Recent(10);
        recent.Count.ShouldBe(1);
        recent[0].Output.ShouldBe("fresh");
    }

    [Fact]
    public void 超过条数上限_只保留最近记录()
    {
        using var temp = new TempStore();
        for (var i = 0; i < 5; i++)
            temp.Store.Add(Entry($"entry-{i}"));

        temp.Store.Configure(3, 0);

        var recent = temp.Store.Recent(10);
        recent.Count.ShouldBe(3);
        recent[0].Output.ShouldBe("entry-4");
        recent[2].Output.ShouldBe("entry-2");
    }

    [Fact]
    public void 清理后不保留空文件()
    {
        using var temp = new TempStore();
        temp.Store.Add(Entry("old", DateTimeOffset.Now.AddDays(-60)));

        temp.Store.Configure(500, 30);

        File.Exists(temp.Path).ShouldBeFalse();
    }

    [Fact]
    public void 启动时按保留策略清理()
    {
        using var temp = new TempStore();
        temp.Store.Add(Entry("old", DateTimeOffset.Now.AddDays(-60)));
        temp.Store.Add(Entry("fresh", DateTimeOffset.Now));

        var restarted = new JsonlHistoryStore(temp.Path, new NullLog(), 500, 30);

        var recent = restarted.Recent(10);
        recent.Count.ShouldBe(1);
        recent[0].Output.ShouldBe("fresh");
    }
}
