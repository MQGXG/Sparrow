// ClipboardPanelWindowTests 分部类：默认外观、透明度、边框与结束菜单。
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
    public void 搜索行默认收起_点标题行搜索才展开()
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

            var searchRow = window.FindControl<Grid>("SearchRow");
            searchRow.ShouldNotBeNull("搜索行要有名字，测试和焦点逻辑都靠它");
            searchRow.IsVisible.ShouldBeFalse("默认收起，这一行留给列表");

            var toggle = window.FindControl<Button>("SearchToggle");
            toggle.ShouldNotBeNull("标题行要有搜索开关");

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            searchRow.IsVisible.ShouldBeTrue("点一次展开");

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            searchRow.IsVisible.ShouldBeFalse("再点收起");
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
    public void 图钉切换面板置顶并写回设置()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var settingsPath = System.IO.Path.Combine(directory, "settings.json");
            var settingsStore = new Sparrow.App.Settings.SettingsStore(settingsPath, new NullLog());

            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("hello");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow
            {
                DataContext = viewModel,
                SettingsStore = settingsStore,
                Settings = settingsStore.Load(),
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.Topmost.ShouldBeFalse("默认不置顶");

            var pin = window.FindControl<Button>("PinButton");
            pin.ShouldNotBeNull("标题行要有图钉");

            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            window.Topmost.ShouldBeTrue("点一下置顶");
            settingsStore.Load().ClipboardPanelTopmost.ShouldBeTrue("置顶要写回 settings.json");

            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            window.Topmost.ShouldBeFalse("再点取消");
            settingsStore.Load().ClipboardPanelTopmost.ShouldBeFalse();
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
    public void 工具条默认透明且不参与命中()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());

            // 两条：面板呼出时第一行是选中的，得靠第二行测默认态
            store.AddText("hello world");
            store.AddText("second item");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("clip-row")
                    && !border.Classes.Contains("selected"));
            row.ShouldNotBeNull("面板呼出时第一行是选中的，要测默认态得找没选中的行");

            var actions = row!.GetVisualDescendants()
                .OfType<Border>()
                .First(border => border.Classes.Contains("row-actions"));

            // headless 的虚拟指针默认压在原点，可能正好悬停在第一行上；
            // 先把它挪开，否则 :pointerover 样式会把工具条显示出来，测的就不是默认态了。
            window.MouseMove(new Point(-1000, -1000));
            Dispatcher.UIThread.RunJobs();

            row!.IsPointerOver.ShouldBeFalse("前提：指针必须不在行上");

            // Opacity 带 140ms 过渡，headless 不推进时钟，不断言它的中间值；
            // 真正要防的是「看不见却还能被点到」，IsHitTestVisible 没有过渡，是瞬时的。
            actions.IsHitTestVisible.ShouldBeFalse(
                $"透明时必须不参与命中，否则看不见的按钮还能被点到；" +
                $"实际 Hit={actions.IsHitTestVisible} Opacity={actions.Opacity}");

            row.Classes.Contains("selected").ShouldBeFalse();
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
    public void 清空入口收进更多菜单()
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

            var clearButton = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => AutomationProperties.GetName(button) == "清空");
            clearButton.ShouldBeNull("搜索行不该再放清空：一行六个控件太挤");

            var more = window.GetVisualDescendants()
                .OfType<Button>()
                .First(button => AutomationProperties.GetName(button) == "更多");

            var flyout = more.Flyout as MenuFlyout;
            flyout.ShouldNotBeNull("⋯ 要有菜单");

            var headers = flyout!.Items
                .OfType<MenuItem>()
                .Select(item => item.Header as string)
                .ToList();

            headers.ShouldContain("清空全部历史…");
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
    public void 详情遮罩完全不透明_不透出下面的列表()
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
            Dispatcher.UIThread.RunJobs();

            var layer = window.FindControl<Panel>("DetailLayer");
            layer.ShouldNotBeNull();

            var backdrop = layer!.Children.OfType<Border>().FirstOrDefault();
            backdrop.ShouldNotBeNull("详情层第一个子元素应该是遮罩");

            var brush = backdrop.Background;
            brush.ShouldNotBeNull("遮罩必须有背景，否则列表会直接透出来");

            // XAML 会把 #AARRGGBB 解析成 ImmutableSolidColorBrush，不是 SolidColorBrush
            var color = brush switch
            {
                SolidColorBrush solid => solid.Color,
                ImmutableSolidColorBrush immutable => immutable.Color,
                _ => throw new Shouldly.ShouldAssertException(
                    $"遮罩背景不是纯色画刷，实际是 {brush.GetType().FullName}"),
            };

            var alpha = color.A;
            alpha.ShouldBe(
                (byte)0xFF,
                $"遮罩必须完全不透明，半透明会让下面的列表透出来（当前 alpha=0x{alpha:X2}）");
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
