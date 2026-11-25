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

/// <summary>热键呼出的剪贴板面板：只做搜索 + 列表，回车 / 双击复制后收起。</summary>
public sealed partial class ClipboardPanelWindowTests
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
    public void 剪贴板面板_长列表只实现在视口内的卡片()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());

            for (var index = 0; index < 80; index++)
                store.AddText($"第 {index} 条内容");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();

            Dispatcher.UIThread.RunJobs();

            // 虚拟化：80 条历史只应实现视口内的那几张卡片
            var realized = window.GetVisualDescendants()
                .OfType<Border>()
                .Count(border => border.Classes.Contains("clip-row"));

            realized.ShouldBeGreaterThan(0);
            realized.ShouldBeLessThan(40, "虚拟化失效：整份历史都被实例化了");
            viewModel.Rows.Count.ShouldBe(80 + viewModel.Groups.Count);
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
    public async Task 剪贴板面板_工具条粘贴按钮_先收起面板()
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

            var paste = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => button.Tag is ClipboardEntryViewModel
                    && ToolTip.GetTip(button) as string == "粘贴到刚才的应用");
            paste.ShouldNotBeNull("条目的悬停工具条应提供「粘贴到刚才的应用」");

            paste!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            window.IsVisible.ShouldBeFalse("粘贴前要先收起面板，把焦点还给目标窗口");

            // 处理器等焦点切换后再写剪贴板；等续体跑完，别让它落到测试清理之后
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();
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
