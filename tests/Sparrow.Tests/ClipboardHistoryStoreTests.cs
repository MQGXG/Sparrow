using System.Text.Json;
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
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sparrow-tests",
                Guid.NewGuid().ToString("N"));

            Path = System.IO.Path.Combine(DirectoryPath, "clipboard.db");
            LegacyPath = System.IO.Path.Combine(DirectoryPath, "clipboard-history.jsonl");
            ImagesDirectory = System.IO.Path.Combine(DirectoryPath, "clipboard-images");
            Store = new ClipboardHistoryStore(Path, new NullLog());
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public string LegacyPath { get; }

        public string ImagesDirectory { get; }

        public ClipboardHistoryStore Store { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
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
    public void 重启后_从数据库恢复记录()
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
    public void 搜索_中文三字走全文索引()
    {
        using var temp = new TempStore();
        temp.Store.AddText("今天要买牛奶和面包");

        temp.Store.UsesFullTextIndex.ShouldBeTrue(
            "内置 SQLite 应支持 FTS5 trigram；不可用时搜索会退化，但功能仍可用");

        temp.Store.Search("买牛奶", 10).ShouldHaveSingleItem()
            .Text.ShouldBe("今天要买牛奶和面包");
        temp.Store.Search("牛奶", 10).ShouldHaveSingleItem(); // 两字走 LIKE 兜底
    }

    [Fact]
    public void 搜索_全文索引大小写不敏感()
    {
        using var temp = new TempStore();
        temp.Store.AddText("HELLO SPARROW");

        temp.Store.UsesFullTextIndex.ShouldBeTrue();
        temp.Store.Search("hello", 10).ShouldHaveSingleItem();
    }

    [Fact]
    public void 迁移_旧JSONL导入并保留字段()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var legacy = Path.Combine(directory, "clipboard-history.jsonl");
        var database = Path.Combine(directory, "clipboard.db");

        File.WriteAllLines(legacy,
        [
            SerializeEntry("带标签的旧记录", DateTimeOffset.Now.AddHours(-2), favorite: true,
                tags: ["工作"], sourceApp: "chrome"),
            SerializeEntry("普通旧记录", DateTimeOffset.Now.AddHours(-1), favorite: false)
        ]);

        try
        {
            var store = new ClipboardHistoryStore(database, new NullLog());

            store.Count.ShouldBe(2);

            var favorite = store.Recent(10).Single(entry => entry.Text == "带标签的旧记录");
            favorite.Favorite.ShouldBeTrue();
            favorite.Tags.ShouldBe(["工作"]);
            favorite.SourceApp.ShouldBe("chrome");

            // 迁移成功后旧文件改名备份，不再参与下次启动
            File.Exists(legacy).ShouldBeFalse();
            File.Exists(legacy + ".bak").ShouldBeTrue();

            // 再次启动不会重复导入
            new ClipboardHistoryStore(database, new NullLog()).Count.ShouldBe(2);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    [Fact]
    public void 迁移_损坏行跳过且不影响其余记录()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var legacy = Path.Combine(directory, "clipboard-history.jsonl");
        File.WriteAllLines(legacy,
        [
            SerializeEntry("正常记录", DateTimeOffset.Now, favorite: false),
            "{这不是JSON"
        ]);

        try
        {
            var store = new ClipboardHistoryStore(Path.Combine(directory, "clipboard.db"), new NullLog());

            store.Count.ShouldBe(1);
            store.Recent(1)[0].Text.ShouldBe("正常记录");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    [Fact]
    public void 自动标签_识别链接邮箱颜色JSON路径()
    {
        using var temp = new TempStore();

        temp.Store.AddText("https://example.com/a");
        temp.Store.AddText("someone@example.com");
        temp.Store.AddText("#E5A44C");
        temp.Store.AddText("""{"a":1}""");
        temp.Store.AddText(@"C:\Windows\notepad.exe");

        var entries = temp.Store.Recent(5);

        entries.Select(entry => entry.AutoTags.Single()).ShouldBe(
            ["路径", "JSON", "颜色", "邮箱", "链接"]);
    }

    [Fact]
    public void 自动标签_普通句子不误判()
    {
        using var temp = new TempStore();

        temp.Store.AddText("看看这个 https://example.com 写得不错");

        temp.Store.Recent(1)[0].AutoTags.ShouldBeEmpty();
    }

    [Fact]
    public void 收藏_不被容量上限清理()
    {
        using var temp = new TempStore();
        var store = new ClipboardHistoryStore(temp.Path, new NullLog(), maxEntries: 2);

        store.AddText("要留下的");
        var favoriteId = store.Recent(1)[0].Id;
        store.SetFavorite(favoriteId, true).ShouldBeTrue();

        store.AddText("第二条");
        store.AddText("第三条");
        store.AddText("第四条");

        // 非收藏只留 2 条；收藏的始终在
        store.Count.ShouldBe(3);
        store.Recent(10).ShouldContain(entry => entry.Id == favoriteId);
    }

    [Fact]
    public void 手动标签_持久化并参与搜索()
    {
        using var temp = new TempStore();
        temp.Store.AddText("一段需要归档的文字");

        var id = temp.Store.Recent(1)[0].Id;
        temp.Store.SetTags(id, ["工作", "重要"]).ShouldBeTrue();

        var reloaded = new ClipboardHistoryStore(temp.Path, new NullLog());

        reloaded.Recent(1)[0].Tags.ShouldBe(["工作", "重要"], ignoreOrder: true);
        reloaded.AllTags().ShouldBe(["工作", "重要"], ignoreOrder: true);
        reloaded.Search("重要", 10).ShouldHaveSingleItem().Id.ShouldBe(id);
    }

    [Fact]
    public void 重复复制_保留收藏与标签()
    {
        using var temp = new TempStore();
        temp.Store.AddText("重复内容");

        var id = temp.Store.Recent(1)[0].Id;
        temp.Store.SetFavorite(id, true);
        temp.Store.SetTags(id, ["保留"]);

        // 再复制一次同样内容：合并旧条目，但收藏和标签不能丢
        temp.Store.AddText("重复内容");

        var entry = temp.Store.Recent(1).ShouldHaveSingleItem();
        entry.Favorite.ShouldBeTrue();
        entry.Tags.ShouldBe(["保留"], ignoreOrder: true);
    }

    [Fact]
    public void 删除条目_图片文件一起删()
    {
        using var temp = new TempStore();
        temp.Store.AddImage(FakePng());

        var id = temp.Store.Recent(1)[0].Id;
        Directory.GetFiles(temp.ImagesDirectory).ShouldHaveSingleItem();

        temp.Store.Delete(id).ShouldBeTrue();

        temp.Store.Count.ShouldBe(0);
        Directory.GetFiles(temp.ImagesDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void 来源应用_持久化并参与搜索()
    {
        using var temp = new TempStore();
        temp.Store.AddText("来自浏览器的内容", "chrome");

        var reloaded = new ClipboardHistoryStore(temp.Path, new NullLog());

        reloaded.Recent(1)[0].SourceApp.ShouldBe("chrome");
        reloaded.AllSourceApps().ShouldBe(["chrome"]);
        reloaded.Search("chrome", 10).ShouldHaveSingleItem();
    }

    [Fact]
    public void 撤销删除_恢复收藏标签与内容()
    {
        using var temp = new TempStore();
        temp.Store.AddText("会被删除再恢复");

        var entry = temp.Store.Recent(1)[0];
        temp.Store.SetFavorite(entry.Id, true);
        temp.Store.SetTags(entry.Id, ["保留"]);

        var snapshot = temp.Store.Recent(1)[0];
        temp.Store.Delete(snapshot.Id).ShouldBeTrue();
        temp.Store.Count.ShouldBe(0);

        temp.Store.Restore(snapshot).ShouldBeTrue();

        var restored = temp.Store.Recent(1)[0];
        restored.Text.ShouldBe("会被删除再恢复");
        restored.Favorite.ShouldBeTrue();
        restored.Tags.ShouldBe(["保留"], ignoreOrder: true);
    }

    [Fact]
    public void 撤销删除_恢复图片内容()
    {
        using var temp = new TempStore();
        temp.Store.AddImage(FakePng());

        var entry = temp.Store.Recent(1)[0];
        var png = temp.Store.LoadImage(entry).ShouldNotBeNull();

        temp.Store.Delete(entry.Id);
        temp.Store.Restore(entry, png).ShouldBeTrue();

        var restored = temp.Store.Recent(1)[0];
        restored.Kind.ShouldBe(Sparrow.Plugin.Sdk.ClipboardEntryKind.Image);
        temp.Store.LoadImage(restored).ShouldNotBeNull();
    }

    [Fact]
    public void 按天数清理_过期条目被移除但收藏保留()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var legacy = Path.Combine(directory, "clipboard-history.jsonl");

        var old = DateTimeOffset.Now.AddDays(-10);
        File.WriteAllLines(legacy,
        [
            SerializeEntry("过期的", old, favorite: false),
            SerializeEntry("过期但收藏", old, favorite: true),
            SerializeEntry("昨天的", DateTimeOffset.Now.AddDays(-1), favorite: false)
        ]);

        var store = new ClipboardHistoryStore(
            Path.Combine(directory, "clipboard.db"),
            new NullLog(),
            maxEntries: 200,
            retentionDays: 7,
            maxBytes: 0);

        // 过期的非收藏被删；7 天内的和收藏都留下
        store.Count.ShouldBe(2);
        store.Recent(10).Select(entry => entry.Text)
            .ShouldBe(["昨天的", "过期但收藏"], ignoreOrder: true);
    }

    [Fact]
    public void 按容量清理_从最旧开始删但收藏保留()
    {
        using var temp = new TempStore();
        var store = new ClipboardHistoryStore(temp.Path, new NullLog(), maxEntries: 200, retentionDays: 0, maxBytes: 30);

        store.AddText("favorite-clip"); // 13
        var favoriteId = store.Recent(1)[0].Id;
        store.SetFavorite(favoriteId, true);

        store.AddText("second-clip-1"); // 13
        store.AddText("third-clip-12"); // 13

        // 总量 39 > 30：从最旧的非收藏开始删，收藏始终在
        store.Recent(10).ShouldContain(entry => entry.Id == favoriteId);
        store.ApproximateBytes.ShouldBeLessThanOrEqualTo(30);
        store.Count.ShouldBe(2);
    }

    [Fact]
    public void 保留策略改完_立即清理并发通知()
    {
        using var temp = new TempStore();
        var store = new ClipboardHistoryStore(temp.Path, new NullLog());

        store.AddText("第一条内容");
        store.AddText("第二条内容");

        var notified = 0;
        store.Changed += () => notified++;

        store.ConfigureRetention(retentionDays: 1, maxBytes: 5);

        notified.ShouldBe(1);
        store.ApproximateBytes.ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public void 近似字节数_累加所有条目()
    {
        using var temp = new TempStore();
        temp.Store.AddText("12345");
        temp.Store.AddText("6789012345");

        temp.Store.ApproximateBytes.ShouldBe(15);
    }

    private static string SerializeEntry(
        string text,
        DateTimeOffset createdAt,
        bool favorite,
        string[]? tags = null,
        string? sourceApp = null) =>
        JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"),
            kind = 0,
            preview = text,
            text,
            paths = Array.Empty<string>(),
            contentHash = "HASH-" + text,
            byteSize = text.Length,
            createdAt,
            favorite,
            tags = tags ?? Array.Empty<string>(),
            sourceApp
        });

    [Fact]
    public void 变更通知_新增与收藏都会触发()
    {
        using var temp = new TempStore();
        var notifications = 0;
        temp.Store.Changed += () => notifications++;

        temp.Store.AddText("触发一次");
        var id = temp.Store.Recent(1)[0].Id;
        temp.Store.SetFavorite(id, true);

        notifications.ShouldBe(2);
    }

    [Fact]
    public void 列表查询只带前2048字符_全文按需读取()
    {
        using var temp = new TempStore();
        var text = new string('a', 3000);
        temp.Store.AddText(text);

        var listEntry = temp.Store.RecentForList(1)[0];
        listEntry.Text!.Length.ShouldBe(2048);
        temp.Store.LoadText(listEntry.Id).ShouldBe(text);

        // 插件走的公开方法行为不变：仍然拿到全文
        temp.Store.Recent(1)[0].Text.ShouldBe(text);
    }

    [Fact]
    public void 预览_换行归一化并截断到80字符()
    {
        using var temp = new TempStore();
        var text = "  " + new string('a', 79) + "\r\n" + new string('b', 10) + "  ";
        temp.Store.AddText(text);

        var preview = temp.Store.Recent(1)[0].Preview;

        // 79 个 a + 一个空格 = 80，再加省略号
        preview.Length.ShouldBe(81);
        preview.ShouldStartWith(new string('a', 79) + " ");
        preview.ShouldEndWith("…");
    }

    [Fact]
    public void 预览_保留内部连续空格()
    {
        using var temp = new TempStore();
        temp.Store.AddText("a\r\nb");

        temp.Store.Recent(1)[0].Preview.ShouldBe("a  b");
    }
}
