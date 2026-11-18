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
