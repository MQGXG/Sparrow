using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 剪贴板页的筛选 / 收藏 / 标签 / 删除。
/// 不测 Win32：数据由 store 注入，页面只负责读写这份数据。
/// </summary>
public sealed class ClipboardHistoryViewModelTests : IDisposable
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }
        public byte[]? ImagePng { get; set; }
        public string? GetText() => Text;
        public byte[]? GetImagePng() => ImagePng;
        public bool SetText(string text)
        {
            Text = text;
            return true;
        }
        public bool SetImagePng(byte[] png)
        {
            ImagePng = png;
            return true;
        }
    }

    private sealed class FakeExclusions : IClipboardExclusionStore
    {
        public List<string> Apps { get; } = [];
        public IReadOnlyList<string> ExcludedApps => Apps;
        public int Saves { get; private set; }
        public void SaveExcludedApps(IReadOnlyList<string> apps)
        {
            Apps.Clear();
            Apps.AddRange(apps);
            Saves++;
        }
    }

    private readonly string _directory;
    private readonly ClipboardHistoryStore _store;
    private readonly FakeClipboard _clipboard = new();
    private readonly ClipboardHistoryViewModel _viewModel;

    public ClipboardHistoryViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        _store = new ClipboardHistoryStore(Path.Combine(_directory, "history.jsonl"), new NullLog());
        _viewModel = new ClipboardHistoryViewModel(_store, _clipboard);
    }

    [Fact]
    public void 打开页_自动选中最新一条()
    {
        _store.AddText("第一条");
        _store.AddText("第二条");

        var viewModel = new ClipboardHistoryViewModel(_store, _clipboard);

        viewModel.Items.Count.ShouldBe(2);
        viewModel.SelectedItem!.Preview.ShouldBe("第二条");
        viewModel.HasDetail.ShouldBeTrue();
        viewModel.DetailText.ShouldBe("第二条");
    }

    [Fact]
    public void 延迟刷新_构造时不加载()
    {
        _store.AddText("延迟加载");

        var viewModel = new ClipboardHistoryViewModel(
            _store, _clipboard, refreshOnCreate: false);

        viewModel.Items.ShouldBeEmpty();

        viewModel.Refresh();

        viewModel.Items.ShouldHaveSingleItem().Preview.ShouldBe("延迟加载");
    }

    [Fact]
    public void 释放派生状态_保留查询并在刷新后恢复选中()
    {
        _store.AddText("第一条");
        _store.AddText("第二条");
        _viewModel.Refresh();

        var selectedId = _viewModel.SelectedItem!.Id;

        _viewModel.ReleaseTransientState();

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.Rows.ShouldBeEmpty();
        _viewModel.Groups.ShouldBeEmpty();
        _viewModel.DetailText.ShouldBeNull();
        _viewModel.HasItems.ShouldBeFalse();

        _viewModel.Refresh();

        _viewModel.Items.Count.ShouldBe(2);
        _viewModel.SelectedItem!.Id.ShouldBe(selectedId);
    }

    [Fact]
    public void 刷新后_保留当前选中的条目()
    {
        _store.AddText("第一条");
        _viewModel.Refresh();

        var selected = _viewModel.SelectedItem!;
        _store.AddText("后来居上");

        _viewModel.SelectedItem!.Id.ShouldBe(selected.Id);
    }

    [Fact]
    public void 类型筛选_链接只留链接()
    {
        _store.AddText("https://example.com");
        _store.AddText("普通文字");

        var link = _viewModel.Kinds.Single(kind => kind.Name == "链接");
        _viewModel.SelectKind(link);

        _viewModel.Items.ShouldHaveSingleItem().Preview.ShouldBe("https://example.com");
    }

    [Fact]
    public void 收藏_排在前面且可筛选()
    {
        _store.AddText("旧的");
        var oldId = _store.Recent(1)[0].Id;
        _store.AddText("新的");

        _viewModel.Refresh();
        var old = _viewModel.Items.Single(item => item.Id == oldId);
        _viewModel.ToggleFavorite(old);

        _viewModel.Items[0].Id.ShouldBe(oldId);

        var favorites = _viewModel.Kinds.Single(kind => kind.Name == "收藏");
        _viewModel.SelectKind(favorites);

        _viewModel.Items.ShouldHaveSingleItem().Id.ShouldBe(oldId);
    }

    [Fact]
    public void 标签_添加与移除()
    {
        _store.AddText("需要归档");
        _viewModel.Refresh();

        _viewModel.AddTag("工作");
        _viewModel.AddTag("重要");

        _viewModel.SelectedItem!.Tags.ShouldBe(["工作", "重要"], ignoreOrder: true);
        _viewModel.TagFilters.Any(filter => filter.Name == "工作").ShouldBeTrue();

        _viewModel.RemoveTag(_viewModel.SelectedItem, "工作");

        _viewModel.SelectedItem!.Tags.ShouldBe(["重要"]);
    }

    [Fact]
    public void 删除_清掉当前选中()
    {
        _store.AddText("要删除的");
        _viewModel.Refresh();

        _viewModel.Delete(_viewModel.SelectedItem!);

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.SelectedItem.ShouldBeNull();
        _store.Count.ShouldBe(0);
    }

    [Fact]
    public void 搜索_匹配正文与标签()
    {
        _store.AddText("alpha 内容");
        _store.AddText("beta 内容");

        _viewModel.Query = "alpha";
        _viewModel.Items.ShouldHaveSingleItem().Preview.ShouldContain("alpha");

        _viewModel.Query = string.Empty;
        _viewModel.Refresh();
        _viewModel.AddTag("特殊标签");
        _viewModel.Query = "特殊标签";

        _viewModel.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public void 复制文本_写回剪贴板()
    {
        _store.AddText("复制我");
        _viewModel.Refresh();

        _viewModel.Copy(_viewModel.SelectedItem!);

        _clipboard.Text.ShouldBe("复制我");
    }

    [Fact]
    public void 清空需要二次确认()
    {
        _store.AddText("x");
        _viewModel.Refresh();

        _viewModel.RequestClear();
        _viewModel.IsClearConfirmVisible.ShouldBeTrue();
        _store.Count.ShouldBe(1);

        _viewModel.ConfirmClear();

        _viewModel.IsClearConfirmVisible.ShouldBeFalse();
        _store.Count.ShouldBe(0);
        _viewModel.Items.ShouldBeEmpty();
    }

    [Fact]
    public void 队列_加入移出并重新编号()
    {
        _store.AddText("第一条");
        _store.AddText("第二条");
        _viewModel.Refresh();

        var first = _viewModel.Items[1];
        var second = _viewModel.Items[0];

        _viewModel.ToggleQueue(first);
        _viewModel.ToggleQueue(second);

        _viewModel.HasQueue.ShouldBeTrue();
        _viewModel.Queue.Select(item => item.Number).ShouldBe([1, 2]);
        first.IsQueued.ShouldBeTrue();
        first.QueueBadge.ShouldBe("1");

        // 移出第一项：剩下那项重新编号为 1
        _viewModel.RemoveQueueItem(_viewModel.Queue[0]);
        _viewModel.Queue.ShouldHaveSingleItem().Number.ShouldBe(1);
        second.IsQueued.ShouldBeTrue();
        second.QueueBadge.ShouldBe("1");

        _viewModel.ClearQueue();
        _viewModel.HasQueue.ShouldBeFalse();
        _viewModel.Items.ShouldAllBe(item => !item.IsQueued);
    }

    [Fact]
    public void 取队列下一条_文本并前进()
    {
        _store.AddText("第一条");
        _store.AddText("第二条");
        _viewModel.Refresh();

        _viewModel.ToggleQueue(_viewModel.Items[1]);
        _viewModel.ToggleQueue(_viewModel.Items[0]);

        var payload = _viewModel.TakeNextPaste();

        payload.ShouldNotBeNull();
        payload!.Text.ShouldBe("第一条");
        _viewModel.Queue.ShouldHaveSingleItem().Entry.Text.ShouldBe("第二条");

        var second = _viewModel.TakeNextPaste();
        second!.Text.ShouldBe("第二条");
        _viewModel.HasQueue.ShouldBeFalse();
        _viewModel.TakeNextPaste().ShouldBeNull();
    }

    [Fact]
    public void 取队列下一条_图片以PNG返回()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        _store.AddImage(png);
        _viewModel.Refresh();

        _viewModel.ToggleQueue(_viewModel.Items[0]);

        var payload = _viewModel.TakeNextPaste();

        payload!.Kind.ShouldBe(Sparrow.Plugin.Sdk.ClipboardEntryKind.Image);
        payload.Png!.SequenceEqual(png).ShouldBeTrue();
    }

    [Fact]
    public void 复制下一条_写入剪贴板()
    {
        _store.AddText("要粘贴的内容");
        _viewModel.Refresh();

        _viewModel.ToggleQueue(_viewModel.Items[0]);
        var paste = new ClipboardPasteService(_clipboard);
        var viewModel = new ClipboardHistoryViewModel(_store, _clipboard, paste);
        viewModel.ToggleQueue(viewModel.Items[0]);

        viewModel.PasteNext(simulate: false).ShouldBeTrue();

        _clipboard.Text.ShouldBe("要粘贴的内容");
        viewModel.HasQueue.ShouldBeFalse();
    }

    [Fact]
    public void 拖出载荷_文本与文件()
    {
        _store.AddText("拖我出去");
        _viewModel.Refresh();

        var text = _viewModel.GetDragPayload(_viewModel.Items[0]);
        text!.Text.ShouldBe("拖我出去");

        _store.AddFiles([@"C:\a.txt", @"C:\b.pdf"]);
        _viewModel.Refresh();

        var files = _viewModel.GetDragPayload(_viewModel.Items[0]);
        files!.Paths.Count.ShouldBe(2);
    }

    [Fact]
    public void 删除条目_同时移出队列()
    {
        _store.AddText("要删除的");
        _viewModel.Refresh();

        _viewModel.ToggleQueue(_viewModel.Items[0]);
        _viewModel.Delete(_viewModel.Items[0]);

        _viewModel.HasQueue.ShouldBeFalse();
    }

    [Fact]
    public void 来源筛选_只留选中应用()
    {
        _store.AddText("来自浏览器", "chrome");
        _store.AddText("来自编辑器", "Code");
        _viewModel.Refresh();

        _viewModel.SourceFilters.ShouldContain("chrome");
        _viewModel.SourceFilters.ShouldContain("Code");

        _viewModel.SelectedSource = "chrome";

        _viewModel.Items.ShouldHaveSingleItem().Preview.ShouldBe("来自浏览器");
    }

    [Fact]
    public void 时间分组_收藏固定在最前()
    {
        _store.AddText("普通一条");
        _store.AddText("收藏一条");
        _viewModel.Refresh();

        var favorite = _viewModel.Items[0];
        _viewModel.ToggleFavorite(favorite);

        _viewModel.Groups[0].Title.ShouldBe("收藏");
        _viewModel.Groups[0].Items.ShouldHaveSingleItem().Id.ShouldBe(favorite.Id);
        _viewModel.Groups.Skip(1).ShouldContain(group => group.Title == "今天");
    }

    [Fact]
    public void 撤销删除_恢复条目与标签()
    {
        _store.AddText("删除后再恢复");
        _viewModel.Refresh();

        _viewModel.AddTag("重要");
        _viewModel.Delete(_viewModel.Items[0]);

        _viewModel.Items.ShouldBeEmpty();
        _viewModel.CanUndoDelete.ShouldBeTrue();

        _viewModel.UndoDelete();

        _viewModel.CanUndoDelete.ShouldBeFalse();
        var restored = _viewModel.Items.ShouldHaveSingleItem();
        restored.Entry.Text.ShouldBe("删除后再恢复");
        restored.Tags.ShouldBe(["重要"], ignoreOrder: true);
    }

    [Fact]
    public void 来源排除_切换名单并给出状态()
    {
        _store.AddText("来自浏览器", "chrome");

        var exclusions = new FakeExclusions();
        var viewModel = new ClipboardHistoryViewModel(_store, _clipboard, pasteService: null, exclusions: exclusions);
        viewModel.Refresh();

        viewModel.SelectedSource = "chrome";
        viewModel.CanToggleExclusion.ShouldBeTrue();
        viewModel.ExclusionButtonLabel.ShouldContain("不记录 chrome");

        viewModel.ToggleSourceExclusion();

        exclusions.Apps.ShouldContain("chrome");
        exclusions.Saves.ShouldBe(1);
        viewModel.StatusText.ShouldContain("已排除 chrome");
        viewModel.ExclusionButtonLabel.ShouldContain("恢复记录 chrome");

        viewModel.ToggleSourceExclusion();

        exclusions.Apps.ShouldBeEmpty();
        viewModel.StatusText.ShouldContain("已恢复记录 chrome");
    }

    [Fact]
    public void 概要_显示条数与占用容量()
    {
        _store.AddText("第一条内容");
        _store.AddText("第二条内容");

        _viewModel.Refresh();

        _viewModel.Summary.ShouldContain("条");
        _viewModel.Summary.ShouldContain("MB");
    }

    [Fact]
    public void 扁平行_分组头与条目交替且条数与分组一致()
    {
        _store.AddText("第一条内容");
        _store.AddText("第二条内容");

        _viewModel.Refresh();

        // 拍平集合是虚拟化列表的数据源：每个分组一条标题行 + 该组的所有条目
        var expected = _viewModel.Groups.Sum(group => group.Items.Count + 1);
        _viewModel.Rows.Count.ShouldBe(expected);

        var first = _viewModel.Rows[0].ShouldBeOfType<ClipboardGroupViewModel>();
        _viewModel.Rows[1].ShouldBeOfType<ClipboardEntryViewModel>();
        _viewModel.Rows[1].ShouldBe(_viewModel.Items[0]);

        // 分组头数量等于 Rows 里的标题行数量
        _viewModel.Rows.OfType<ClipboardGroupViewModel>().Count().ShouldBe(_viewModel.Groups.Count);
    }

    [Fact]
    public void 扁平行_重新筛选后跟着重建()
    {
        _store.AddText("chrome 收藏的内容");
        _store.AddText("另一条");

        _viewModel.Refresh();
        var before = _viewModel.Rows.Count;

        _viewModel.Query = "chrome";
        _viewModel.Refresh();

        _viewModel.Rows.Count.ShouldBeLessThan(before);
        _viewModel.Rows.OfType<ClipboardEntryViewModel>().Count()
            .ShouldBe(_viewModel.Items.Count);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 临时目录留给系统清理
        }
    }
}
