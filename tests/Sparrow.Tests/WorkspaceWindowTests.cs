using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;

namespace Sparrow.Tests;

/// <summary>
/// 工作区窗口的导航回归：切换行必须常驻，Esc 必须能从工具台返回。
/// 这两条曾经让用户「用了一个工具就回不到工具墙」。
/// </summary>
public sealed class WorkspaceWindowTests
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

    /// <summary>文件类工具的最小实现：只为验证「选择文件…」入口的可见性。</summary>
    private sealed class FilesTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "test.files",
            Name = "批量测试",
            Category = "ocr",
            Accepts = InputShape.Files,
            Produces = OutputShape.Text
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.InvalidInput("未实现"));
    }

    /// <summary>图片集工具的最小实现：只为验证缩略图条与「添加图片…」入口。</summary>
    private sealed class ImagesTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "test.images",
            Name = "图片集测试",
            Category = "ocr",
            Accepts = InputShape.Images,
            Produces = OutputShape.Text
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.InvalidInput("未实现"));
    }

    private static (WorkspaceWindow Window, WorkspaceViewModel ViewModel, ToolCatalog Catalog) Create(
        ClipboardHistoryStore? clipboardHistory = null)
    {
        var log = new NullLog();
        var rootPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        var catalog = new ToolCatalog(rootPath, log, new AppSettings());
        var store = new SettingsStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N") + ".json"),
            log);
        var settingsViewModel = new SettingsViewModel(catalog, store, new StartupService(log), new AppSettings());

        var viewModel = new WorkspaceViewModel(
            catalog,
            new FakeClipboard(),
            log,
            new NullHistoryStore(),
            settingsViewModel,
            clipboardHistory: clipboardHistory);
        var window = new WorkspaceWindow { DataContext = viewModel };
        window.Show();

        return (window, viewModel, catalog);
    }

    [AvaloniaFact]
    public void 内容区_承载主导航_标题栏只放品牌与动作()
    {
        var (window, _, catalog) = Create();
        try
        {
            var titleBar = window.FindControl<Grid>("TitleBar");
            titleBar.ShouldNotBeNull();

            var tabRow = window.FindControl<Grid>("WallTabRow");
            tabRow.ShouldNotBeNull();
            tabRow.GetVisualAncestors().OfType<Grid>().ShouldNotContain(titleBar,
                "主导航应该回到内容区，不再占用系统标题栏");
            tabRow.IsEffectivelyVisible.ShouldBeTrue();

            window.FindControl<Button>("TopSearchButton").ShouldNotBeNull();
            window.FindControl<Button>("SettingsButton").ShouldNotBeNull();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 设置页_返回入口在内容区顶部()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsSettingsVisible = true;

            var visibleBacks = window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.IsEffectivelyVisible &&
                                 button.Content is string text &&
                                 text.Contains("返回"))
                .ToList();
            visibleBacks.Count.ShouldBe(1, "设置页应该只有一个返回入口，放在内容区顶部");

            var tabRow = window.FindControl<Grid>("WallTabRow");
            tabRow.ShouldNotBeNull();
            tabRow.IsEffectivelyVisible.ShouldBeFalse("设置页不显示主视图页签");
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 工具台_Esc返回工具墙()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            viewModel.IsDetailOpen.ShouldBeFalse();
            viewModel.IsWallVisible.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 文件类工具_显示选择文件入口()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;
            viewModel.SelectedTool = new ToolItemViewModel(new ToolEntry
            {
                Tool = new FilesTool(),
                Host = NullToolHost.Instance
            });

            viewModel.IsFilesInput.ShouldBeTrue();

            var choose = window.FindControl<Button>("ChooseFilesButton");
            choose.ShouldNotBeNull("文件类工具必须提供选择文件入口");
            choose.IsVisible.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 图片集工具_显示缩略图条与添加入口()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;
            viewModel.SelectedTool = new ToolItemViewModel(new ToolEntry
            {
                Tool = new ImagesTool(),
                Host = NullToolHost.Instance
            });

            viewModel.InputImages.Add(new InputImageViewModel(
                new ImageInput(new byte[] { 1, 2, 3 }, ImageFormat.Png) { Name = "a.png" },
                sourcePath: null));

            viewModel.IsImagesInput.ShouldBeTrue();

            var choose = window.FindControl<Button>("ChooseImagesButton");
            choose.ShouldNotBeNull("图片集工具必须提供添加图片入口");
            choose.IsVisible.ShouldBeTrue();

            var strip = window.FindControl<ItemsControl>("InputImagesStrip");
            strip.ShouldNotBeNull("图片集工具必须显示缩略图条");
            strip.IsVisible.ShouldBeTrue();
            strip.ItemCount.ShouldBe(1);
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 大图预览_Esc只关预览不退出工具页()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;
            viewModel.IsImagePreviewVisible = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            viewModel.IsImagePreviewVisible.ShouldBeFalse();
            viewModel.IsDetailOpen.ShouldBeTrue();
            window.IsVisible.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 剪贴板页_显示记录并保留切换入口()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("https://example.com");

            var (window, viewModel, catalog) = Create(store);
            try
            {
                viewModel.ShowClipboard();

                window.FindControl<Grid>("ClipboardPage")!.IsVisible.ShouldBeTrue();
                window.FindControl<Grid>("WallTabRow")!.IsVisible.ShouldBeTrue();

                viewModel.Clipboard!.Items.ShouldHaveSingleItem()
                    .Preview.ShouldBe("https://example.com");
            }
            finally
            {
                catalog.Dispose();
            }
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
    public void 剪贴板页_行按钮与右键菜单对齐面板()
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

            var (window, viewModel, catalog) = Create(store);
            try
            {
                viewModel.ShowClipboard();
                Dispatcher.UIThread.RunJobs();

                var page = window.FindControl<Grid>("ClipboardPage")!;
                var row = page.GetVisualDescendants()
                    .OfType<Border>()
                    .First(border => border.Classes.Contains("clip-row"));

                row.ContextMenu.ShouldNotBeNull("工作区的行应挂上共享右键菜单");

                var headers = row.ContextMenu!.Items
                    .OfType<MenuItem>()
                    .Select(item => item.Header as string)
                    .ToList();

                headers.ShouldContain("复制");
                headers.ShouldContain("复制为纯文本");
                headers.ShouldContain("复制为");
                headers.ShouldContain("打开");
                headers.ShouldContain("打开所在位置");
                headers.ShouldContain("查看详情");
                headers.ShouldContain("删除");
                headers.ShouldNotContain(
                    "粘贴到刚才的应用",
                    "工作区窗口本身在前台，没有「刚才的应用」可粘贴");

                var tips = row.GetVisualDescendants()
                    .OfType<Button>()
                    .Select(button => button.GetValue(ToolTip.TipProperty)?.ToString())
                    .ToList();

                tips.ShouldContain("复制", "行内也要能直接复制");
                tips.ShouldContain("删除（可撤销）", "行内也要能直接删除");
                tips.ShouldContain(
                    "加入 / 移出顺序粘贴队列（Ctrl+点击整行也可以）",
                    "原有的队列按钮不能被挤掉");
            }
            finally
            {
                catalog.Dispose();
            }
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
    public void 工作区来源显示为图标而不是文字()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("hello world", "some-origin-app");

            var (window, viewModel, catalog) = Create(store);
            try
            {
                viewModel.ShowClipboard();
                Dispatcher.UIThread.RunJobs();

                var page = window.FindControl<Grid>("ClipboardPage")!;

                // 前提：条目确实带来源，否则下面的断言是空过
                viewModel.Clipboard!.Items.ShouldHaveSingleItem()
                    .SourceLabel.ShouldBe("some-origin-app");

                var sourceText = page.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(block => block.Text == "some-origin-app");

                sourceText.ShouldBeNull("来源已换成图标，进程名只留在 ToolTip 里");
            }
            finally
            {
                catalog.Dispose();
            }
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
    public void 设置页_Esc不抢返回()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;
            viewModel.IsSettingsVisible = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            viewModel.IsDetailOpen.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 顶栏_搜索默认收起_点开展开_Esc清空并收起()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            var pill = window.FindControl<Border>("TopSearchPill");
            var box = window.FindControl<TextBox>("TopSearchBox");
            var button = window.FindControl<Button>("TopSearchButton");

            pill.ShouldNotBeNull("顶栏搜索胶囊必须存在");
            box.ShouldNotBeNull("顶栏搜索输入框必须存在");
            button.ShouldNotBeNull("顶栏搜索图标必须存在");

            box!.IsVisible.ShouldBeFalse("搜索默认只占一枚图标，不占整行");
            pill!.Classes.Contains("open").ShouldBeFalse();

            button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            box.IsVisible.ShouldBeTrue("点图标后输入框向左展开");
            pill.Classes.Contains("open").ShouldBeTrue("展开时胶囊显示描边底色");

            viewModel.ToolQuery = "json";

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            box.IsVisible.ShouldBeFalse("Esc 收起搜索");
            pill.Classes.Contains("open").ShouldBeFalse();
            viewModel.ToolQuery.ShouldBeEmpty("收起时清掉过滤词，工具墙恢复全量");
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 顶栏_CtrlF聚焦搜索()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            var box = window.FindControl<TextBox>("TopSearchBox");
            box.ShouldNotBeNull();

            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");

            box!.IsVisible.ShouldBeTrue("Ctrl+F 直接展开并聚焦顶栏搜索");
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 顶栏_设置图标打开设置页()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            var button = window.FindControl<Button>("SettingsButton");
            button.ShouldNotBeNull("设置入口必须是顶栏图标按钮");

            button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            viewModel.IsSettingsVisible.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 剪贴板详情_在页内打开_Esc返回列表()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("https://example.com");

            var (window, viewModel, catalog) = Create(store);
            try
            {
                viewModel.ShowClipboard();
                window.UpdateLayout();

                var button = window.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(control => AutomationProperties.GetName(control) == "查看详情");

                button.ShouldNotBeNull("列表行里应该有「查看详情」按钮");

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var layer = window.FindControl<Panel>("ClipboardDetailLayer");
                layer.ShouldNotBeNull();
                layer.IsVisible.ShouldBeTrue("详情应该盖在剪贴板页上，而不是单独开窗");

                window.OwnedWindows.ShouldBeEmpty();

                var detail = window.FindControl<ClipboardDetailView>("ClipboardDetailViewHost");
                detail.ShouldNotBeNull();
                detail.DataContext.ShouldBeSameAs(viewModel.Clipboard);

                viewModel.Clipboard!.SelectedItem.ShouldNotBeNull();

                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

                layer.IsVisible.ShouldBeFalse("Esc 先退出详情");
                window.IsVisible.ShouldBeTrue("退出详情不等于关掉工具台");
            }
            finally
            {
                catalog.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录可能被系统占用
            }
        }
    }
}
