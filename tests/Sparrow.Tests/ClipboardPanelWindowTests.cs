using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>热键呼出的剪贴板面板：只做搜索 + 列表，回车 / 双击复制后收起。</summary>
public sealed class ClipboardPanelWindowTests
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

    [AvaloniaFact]
    public void 剪贴板面板_回车复制并收起()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("第一条");

            var clipboard = new FakeClipboard();
            var viewModel = new ClipboardHistoryViewModel(store, clipboard);
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

            clipboard.Text.ShouldBe("第一条");
            window.IsVisible.ShouldBeFalse();
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
    public void 剪贴板面板_呼出清空搜索_收起后不污染工作区()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("第一条");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            viewModel.Query = "历史记录";

            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            viewModel.Query.ShouldBeEmpty("面板呼出时应从干净搜索开始");

            window.HidePanel();
            Dispatcher.UIThread.RunJobs();

            viewModel.Query.ShouldBe("历史记录", "面板收起后不能把工作区页的搜索词清掉");
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
    public void 剪贴板面板_队列条可移出与清空()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("第一条");
            store.AddText("第二条");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            viewModel.Items.Count.ShouldBeGreaterThanOrEqualTo(2);
            viewModel.ToggleQueue(viewModel.Items[0]);
            viewModel.ToggleQueue(viewModel.Items[1]);
            Dispatcher.UIThread.RunJobs();

            viewModel.HasQueue.ShouldBeTrue();

            var copyNext = window.FindControl<Button>("QueueCopyNextButton");
            copyNext.ShouldNotBeNull("面板应该和剪贴板页一样提供队列条");
            copyNext!.IsEffectivelyVisible.ShouldBeTrue();

            var chip = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => button.Tag is ClipboardQueueItemViewModel);
            chip.ShouldNotBeNull("队列条应把每条渲染成可点击的 chip");
            chip!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            viewModel.Queue.Count.ShouldBe(1, "点队列 chip 应移出该条");

            var clear = window.FindControl<Button>("QueueClearButton");
            clear.ShouldNotBeNull();
            clear!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            viewModel.HasQueue.ShouldBeFalse("清空只清队列，不影响历史");
            viewModel.Items.Count.ShouldBeGreaterThanOrEqualTo(2);
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
    public void 剪贴板面板_Alt数字直接复制对应条目()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("第一条");
            store.AddText("第二条");
            store.AddText("第三条");

            var clipboard = new FakeClipboard();
            var viewModel = new ClipboardHistoryViewModel(store, clipboard);
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            // 列表按时间倒序：第 1 条是最新的「第三条」
            viewModel.Items[0].Preview.ShouldBe("第三条");

            window.KeyPress(Key.D3, RawInputModifiers.Alt, PhysicalKey.None, null);

            clipboard.Text.ShouldBe("第一条", "Alt+3 应复制列表第 3 条");
            window.IsVisible.ShouldBeFalse("快贴后应收起面板");
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
    public void 剪贴板视图_类型计数与快贴序号()
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

            // 前 9 条有快贴序号，第 10 条没有
            viewModel.Items[0].QuickIndex.ShouldBe(1);
            viewModel.Items[8].QuickIndex.ShouldBe(9);
            viewModel.Items[9].QuickIndex.ShouldBe(0);
            viewModel.Items[9].HasQuickIndex.ShouldBeFalse();

            viewModel.StatusBarText.ShouldContain("10 /");
            viewModel.StatusBarText.ShouldContain("条");
            viewModel.StatusHint.ShouldContain("Alt+1");
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
            row!.ContextMenu.ShouldNotBeNull("行上应挂复制/变换菜单");

            var headers = row.ContextMenu!.Items
                .OfType<MenuItem>()
                .Select(menuItem => menuItem.Header as string)
                .ToList();

            headers.ShouldContain("复制");
            headers.ShouldContain("复制为纯文本");
            headers.ShouldContain("全部大写");
            headers.ShouldContain("驼峰命名");
            headers.Count.ShouldBe(8, "复制 + 7 种变换");
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
    public void 类型chip_空分类隐藏_选中项始终显示()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("只有文本");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());

            viewModel.Kinds.First(kind => kind.Name == "全部").IsShown.ShouldBeTrue();
            viewModel.Kinds.First(kind => kind.Name == "文本").IsShown.ShouldBeTrue();
            viewModel.Kinds.First(kind => kind.Name == "图片").IsShown.ShouldBeFalse("空分类不该占位置");
            viewModel.Kinds.First(kind => kind.Name == "收藏").IsShown.ShouldBeFalse();

            // 选中一个空分类时它必须留下，否则筛选无法取消
            viewModel.SelectKind(viewModel.Kinds.First(kind => kind.Name == "图片"));
            viewModel.Kinds.First(kind => kind.Name == "图片").IsShown.ShouldBeTrue();

            viewModel.CapacityRatio.ShouldBe(0.03, "空库保留 3% 的可视进度");
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
    public void 剪贴板面板_查看详情覆盖在面板上_Esc回到列表()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("第一条");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            var viewButton = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => Equals(button.Content, "查看"));
            viewButton.ShouldNotBeNull("列表行必须提供「查看」入口");
            viewButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var layer = window.FindControl<Panel>("DetailLayer");
            layer.ShouldNotBeNull();
            layer!.IsVisible.ShouldBeTrue("详情应该覆盖在面板内，而不是另开窗口");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            layer.IsVisible.ShouldBeFalse();
            window.IsVisible.ShouldBeTrue("Esc 只返回列表，不应收起面板");
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
