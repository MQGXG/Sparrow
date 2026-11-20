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

    [AvaloniaFact]
    public void 无边框窗口有八个缩放手柄()
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

            window.ExtendClientAreaToDecorationsHint.ShouldBeTrue(
                "不打开这个开关，ElementRole 完全不生效");

            var grips = window.GetVisualDescendants()
                .OfType<Control>()
                .Select(control => WindowDecorationProperties.GetElementRole(control))
                .Where(role => role is
                    WindowDecorationsElementRole.ResizeN
                    or WindowDecorationsElementRole.ResizeS
                    or WindowDecorationsElementRole.ResizeE
                    or WindowDecorationsElementRole.ResizeW
                    or WindowDecorationsElementRole.ResizeNE
                    or WindowDecorationsElementRole.ResizeNW
                    or WindowDecorationsElementRole.ResizeSE
                    or WindowDecorationsElementRole.ResizeSW)
                .Count();

            grips.ShouldBe(8, "四边 + 四角都要有手柄，否则角落拖不动");

            var titleBar = window.GetVisualDescendants()
                .OfType<Control>()
                .Any(control => WindowDecorationProperties.GetElementRole(control)
                    == WindowDecorationsElementRole.TitleBar);

            titleBar.ShouldBeTrue("标题行要标成 TitleBar，交给 OS 处理拖动");
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
    public void 拉伸窗口后详情层仍覆盖整个面板()
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

            // 模拟「详情已打开」
            var layer = window.FindControl<Panel>("DetailLayer")!;
            layer.IsVisible = true;

            window.Width = 700;
            window.Height = 900;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            // 详情层在 Shell 内部，比 Shell 少 2px 边框，所以和父容器比
            var host = (Control)layer.Parent!;

            layer.Bounds.Height.ShouldBe(
                host.Bounds.Height,
                "详情层必须跟着面板一起变高，否则拉伸后下方会露出列表");
            layer.Bounds.Width.ShouldBe(host.Bounds.Width);
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

            // 第一个 row-icon 就是「查看」：三个图标按 查看 / 队列 / 收藏 排列
            var viewButton = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => button.Classes.Contains("row-icon"));
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
