// ClipboardPanelWindowTests 分部类：条目预览与右键菜单。
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

public sealed partial class ClipboardPanelWindowTests
{
    [AvaloniaFact]
    public void 剪贴板视图_类型计数()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());

            for (var index = 1; index <= 10; index++)
                store.AddText($"第 {index} 条");

            store.SetFavorite(store.Recent(1)[0].Id, true);

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());

            viewModel.Kinds.First(kind => kind.Name == "全部").Count.ShouldBe(10);
            viewModel.Kinds.First(kind => kind.Name == "文本").Count.ShouldBe(10);
            viewModel.Kinds.First(kind => kind.Name == "收藏").Count.ShouldBe(1);

            viewModel.StatusBarText.ShouldContain("10 /");
            viewModel.StatusBarText.ShouldContain("条");
            viewModel.StatusHint.ShouldContain("Enter");
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

    [AvaloniaFact]
    public void 剪贴板视图_按变换复制文本()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("hello world again");

            var clipboard = new FakeClipboard();
            var viewModel = new ClipboardHistoryViewModel(store, clipboard);

            viewModel.CopyTransformed(viewModel.Items[0], PasteTransform.Camel).ShouldBeTrue();
            clipboard.Text.ShouldBe("helloWorldAgain");

            viewModel.CopyTransformed(viewModel.Items[0], PasteTransform.Upper).ShouldBeTrue();
            clipboard.Text.ShouldBe("HELLO WORLD AGAIN");
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

    [AvaloniaFact]
    public void 面板行右键菜单提供复制变换()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("hello world again");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("clip-row"));

            row.ShouldNotBeNull("列表行应该渲染出来");
            row!.ContextMenu.ShouldNotBeNull("行上应挂完整菜单");

            var menuItems = row.ContextMenu!.Items.OfType<MenuItem>().ToList();
            var headers = menuItems.Select(menuItem => menuItem.Header as string).ToList();

            // 与悬停工具条能力对齐：复制 / 粘贴 / 打开 / 定位 / 队列 / 收藏 / 详情 / 删除
            headers.ShouldContain("复制");
            headers.ShouldContain("粘贴到刚才的应用");
            headers.ShouldContain("复制为纯文本");
            headers.ShouldContain("复制为");
            headers.ShouldContain("打开");
            headers.ShouldContain("打开所在位置");
            headers.ShouldContain("加入顺序粘贴队列");
            headers.ShouldContain("收藏");
            headers.ShouldContain("查看详情");
            headers.ShouldContain("删除");

            var transforms = menuItems
                .Single(menuItem => (menuItem.Header as string) == "复制为")
                .Items.OfType<MenuItem>()
                .Select(menuItem => menuItem.Header as string)
                .ToList();

            transforms.ShouldBe(
                ["全部大写", "全部小写", "每词首字母大写", "句首大写", "驼峰命名", "去掉首尾空白"],
                "6 种变换收进「复制为」子菜单");
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

    [AvaloniaFact]
    public void 右键菜单按条目刷新可见性与文案()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("hello world");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants()
                .OfType<Border>()
                .First(border => border.Classes.Contains("clip-row"));
            var menu = row.ContextMenu!;
            menu.Open(row);
            Dispatcher.UIThread.RunJobs();

            var items = menu.Items
                .OfType<MenuItem>()
                .ToDictionary(item => item.Header as string ?? string.Empty);

            items["打开"].IsVisible.ShouldBeFalse("普通文本没有打开目标");
            items["打开所在位置"].IsVisible.ShouldBeFalse("文本条目没有可定位的文件");
            items["复制为纯文本"].IsVisible.ShouldBeTrue("文本可以复制为纯文本");
            items["复制为"].IsVisible.ShouldBeTrue("文本可以按变换复制");
            items["加入顺序粘贴队列"].Header.ShouldBe("加入顺序粘贴队列");
            items["收藏"].Header.ShouldBe("收藏");

            menu.Close();
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

    [AvaloniaFact]
    public void 右键菜单对文件显示打开与定位()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var file = System.IO.Path.Combine(directory, "报告.docx");
            File.WriteAllText(file, "doc");

            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddFiles([file]);

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants()
                .OfType<Border>()
                .First(border => border.Classes.Contains("clip-row"));
            var menu = row.ContextMenu!;
            menu.Open(row);
            Dispatcher.UIThread.RunJobs();

            var items = menu.Items
                .OfType<MenuItem>()
                .ToDictionary(item => item.Header as string ?? string.Empty);

            items["打开"].IsVisible.ShouldBeTrue("文件可以用默认程序打开");
            items["打开所在位置"].IsVisible.ShouldBeTrue("文件可以在资源管理器里定位");
            items["复制为纯文本"].IsVisible.ShouldBeFalse("文件没有可复制的纯文本");
            items["复制为"].IsVisible.ShouldBeFalse("文件不能做文本变换");

            menu.Close();
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
}
