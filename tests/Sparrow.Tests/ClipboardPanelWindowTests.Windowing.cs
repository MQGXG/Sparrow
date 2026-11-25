// ClipboardPanelWindowTests 分部类：无边框拖拽、chip 与视图切换。
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
