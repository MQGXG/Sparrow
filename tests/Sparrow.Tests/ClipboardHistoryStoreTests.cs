using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

public sealed class ClipboardHistoryStoreTests
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
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sparrow-tests",
                Guid.NewGuid().ToString("N"));

            Path = System.IO.Path.Combine(directory, "clipboard-history.jsonl");
            ImagesDirectory = System.IO.Path.Combine(directory, "clipboard-images");
            Store = new ClipboardHistoryStore(Path, new NullLog());
        }

        public string Path { get; }

        public string ImagesDirectory { get; }

        public ClipboardHistoryStore Store { get; }

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

    private static byte[] FakePng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4
    ];

    [Fact]
    public void 添加文本_最近的排最前()
    {
        using var temp = new TempStore();

        temp.Store.AddText("第一条");
        temp.Store.AddText("第二条");
        temp.Store.AddText("第三条");

        var recent = temp.Store.Recent(10);
        recent.Count.ShouldBe(3);
        recent[0].Text.ShouldBe("第三条");
        recent[2].Text.ShouldBe("第一条");
        recent[0].Kind.ShouldBe(Sparrow.Plugin.Sdk.ClipboardEntryKind.Text);
    }

    [Fact]
    public void 连续复制相同内容_合并成一条并刷新时间()
    {
        using var temp = new TempStore();

        temp.Store.AddText("重复内容");
        var first = temp.Store.Recent(1)[0];

        temp.Store.AddText("其他内容");
        temp.Store.AddText("重复内容");

        var recent = temp.Store.Recent(10);
        temp.Store.Count.ShouldBe(2);
        recent[0].Text.ShouldBe("重复内容");
        recent[0].CreatedAt.ShouldBeGreaterThan(first.CreatedAt);
    }

    [Fact]
    public void 空文本_不记录()
    {
        using var temp = new TempStore();

        temp.Store.AddText("");
        temp.Store.AddText("   ");

        temp.Store.Count.ShouldBe(0);
    }

    [Fact]
    public void 记录图片_内容存进独立目录且能取回()
    {
        using var temp = new TempStore();
        var png = FakePng();

        temp.Store.AddImage(png);

        var entry = temp.Store.Recent(1)[0];
        entry.Kind.ShouldBe(Sparrow.Plugin.Sdk.ClipboardEntryKind.Image);
        entry.Text.ShouldBeNull();
        entry.ByteSize.ShouldBe(png.Length);

        Directory.EnumerateFiles(temp.ImagesDirectory).ShouldHaveSingleItem();

        var restored = temp.Store.LoadImage(entry);
        restored.ShouldNotBeNull().ShouldBe(png);
    }

    [Fact]
    public void 连续复制相同图片_只保留一份文件()
    {
        using var temp = new TempStore();
        var png = FakePng();

        temp.Store.AddImage(png);
        temp.Store.AddText("中间隔一条");
        temp.Store.AddImage(png);

        temp.Store.Count.ShouldBe(2);
        Directory.EnumerateFiles(temp.ImagesDirectory).ShouldHaveSingleItem();
    }

    [Fact]
    public void 超大图片_跳过不记录()
    {
        using var temp = new TempStore();
        var huge = new byte[33 * 1024 * 1024];

        temp.Store.AddImage(huge);

        temp.Store.Count.ShouldBe(0);
    }

    [Fact]
    public void 记录文件_路径完整保留()
    {
        using var temp = new TempStore();

        temp.Store.AddFiles(["C:\\a.txt", "C:\\b.txt"]);

        var entry = temp.Store.Recent(1)[0];
        entry.Kind.ShouldBe(Sparrow.Plugin.Sdk.ClipboardEntryKind.Files);
        entry.Paths.ShouldBe(["C:\\a.txt", "C:\\b.txt"]);
        entry.Preview.ShouldBe("2 个文件");
    }

    [Fact]
    public void 搜索_命中文本摘要和文件路径()
    {
        using var temp = new TempStore();

        temp.Store.AddText("今天要买牛奶");
        temp.Store.AddText("会议纪要");
        temp.Store.AddFiles(["D:\\报告\\季度总结.xlsx"]);

        temp.Store.Search("牛奶", 10).ShouldHaveSingleItem()
            .Text.ShouldBe("今天要买牛奶");
        temp.Store.Search("季度", 10).ShouldHaveSingleItem()
            .Kind.ShouldBe(Sparrow.Plugin.Sdk.ClipboardEntryKind.Files);
        temp.Store.Search("不存在", 10).ShouldBeEmpty();
        // 空关键字按全部记录返回
        temp.Store.Search("", 10).Count.ShouldBe(3);
    }

    [Fact]
    public void 超条数上限_旧记录和对应图片文件被清理()
    {
        using var temp = new TempStore();
        temp.Store.Configure(3);

        var oldestPng = FakePng();
        temp.Store.AddImage(oldestPng);
        temp.Store.AddText("a");
        temp.Store.AddText("b");
        temp.Store.AddText("c");

        temp.Store.Count.ShouldBe(3);
        temp.Store.Recent(10)[^1].Kind.ShouldBe(Sparrow.Plugin.Sdk.ClipboardEntryKind.Text);
        // 唯一的图片条目被挤出去了，文件也该删掉
        Directory.EnumerateFiles(temp.ImagesDirectory).ShouldBeEmpty();

        temp.Store.LoadImage(new Sparrow.Plugin.Sdk.ClipboardHistoryEntry
        {
            Id = "gone",
            Kind = Sparrow.Plugin.Sdk.ClipboardEntryKind.Image,
            Preview = "",
            CreatedAt = DateTimeOffset.Now
        }).ShouldBeNull();
    }

    [Fact]
    public void 重启后_从JSONL恢复记录()
    {
        using var temp = new TempStore();

        temp.Store.AddText("重启前的内容");
        temp.Store.AddImage(FakePng());

        var reloaded = new ClipboardHistoryStore(temp.Path, new NullLog());

        reloaded.Count.ShouldBe(2);
        reloaded.Recent(10)[1].Text.ShouldBe("重启前的内容");
        reloaded.Recent(10)[0].Kind.ShouldBe(Sparrow.Plugin.Sdk.ClipboardEntryKind.Image);
    }

    [Fact]
    public void 损坏的行_被跳过不影响其余记录()
    {
        using var temp = new TempStore();
        temp.Store.AddText("正常记录");

        File.AppendAllText(temp.Path, "{这不是JSON" + Environment.NewLine);

        var reloaded = new ClipboardHistoryStore(temp.Path, new NullLog());

        reloaded.Count.ShouldBe(1);
        reloaded.Recent(1)[0].Text.ShouldBe("正常记录");
    }
}
