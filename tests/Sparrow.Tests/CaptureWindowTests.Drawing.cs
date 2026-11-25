// CaptureWindowTests 分部类：尺寸角标、标记、拖拽绘制与压感。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Shouldly;
using Sparrow.App.Services;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

public sealed partial class CaptureWindowTests
{
    [Fact]
    public void 工具条_上方被尺寸浮条挡住时叠到浮条上方()
    {
        // 选区贴底：下方放不下，上方又被尺寸浮条占住
        var selection = new Rect(100, 880, 300, 100);
        var badge = new Rect(100, 850, 200, 22);

        var position = CaptureWindow.ComputeToolbarPosition(
            selection, new Size(400, 40), new Size(1280, 1000), badge);

        position.Y.ShouldBe(850 - 40 - 8);
        position.X.ShouldBe(50);
    }

    [Fact]
    public void 工具条_多屏边界按当前显示器钳制()
    {
        // 左屏 1000..1800，选区贴右边缘：不能跨到窗口的虚拟桌面边界
        var position = CaptureWindow.ComputeToolbarPosition(
            new Rect(1700, 100, 80, 50),
            new Size(400, 40),
            new Rect(1000, 0, 800, 600));

        position.X.ShouldBe(1800 - 400 - 8);
    }

    [Fact]
    public void 工具条_下方放不下时翻到选区上方()
    {
        var selection = new Rect(100, 900, 300, 100);
        var position = CaptureWindow.ComputeToolbarPosition(
            selection, new Size(400, 40), new Size(1280, 1000));

        position.Y.ShouldBe(900 - 40 - 8);
        position.X.ShouldBe(50);   // 居中到选区中点（100 + 300/2 - 400/2）
    }

    [Fact]
    public void 工具条_贴边时钳制在窗口内()
    {
        // 选区在最左侧，工具条比选区宽：不能跑到窗口外
        var position = CaptureWindow.ComputeToolbarPosition(
            new Rect(0, 100, 50, 50), new Size(400, 40), new Size(1280, 1000));

        position.X.ShouldBe(8);
        position.Y.ShouldBe(158);
    }

    [AvaloniaFact]
    public void 截图工具条_按语言列表生成翻译菜单()
    {
        var window = new CaptureWindow();
        window.ConfigureTranslateTargets([("中文", "中文"), ("English", "英语")]);

        window.TranslateButton.IsVisible.ShouldBeTrue();

        var menu = window.TranslateButton.Flyout.ShouldBeOfType<MenuFlyout>();

        // 语言项仍在第一层；引擎 / 复制格式 / 标点收进二级菜单
        menu.Items.OfType<MenuItem>().Count(item => item.Header is "中文" or "英语").ShouldBe(2);

        var headers = menu.Items.OfType<MenuItem>()
            .Select(item => item.Header as string)
            .ToArray();

        headers.ShouldContain(header => header!.StartsWith("翻译引擎"));
        headers.ShouldContain(header => header!.StartsWith("复制格式"));
        headers.ShouldContain(header => header!.StartsWith("标点"));

        // 没有语言声明时整个按钮隐藏，避免点了没反应
        window.ConfigureTranslateTargets([]);
        window.TranslateButton.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void 拖动把手_工具条改变位置_选区变动后恢复跟随()
    {
        var window = CreateWithSelection();
        var before = Canvas.GetLeft(window.ActionBar);
        double.IsNaN(before).ShouldBeFalse();

        var handle = window.ToolbarDragHandle
            .TranslatePoint(new Point(6, 10), window)!.Value;

        window.MouseDown(handle, MouseButton.Left);
        window.MouseMove(new Point(handle.X + 120, handle.Y + 90));
        window.MouseUp(new Point(handle.X + 120, handle.Y + 90), MouseButton.Left);

        var moved = Canvas.GetLeft(window.ActionBar);
        moved.ShouldNotBe(before);

        // 选区一动，工具条重新贴回选区中线（参考 resetPositionForSelection：
        // 手动拖开只影响「内容变化」时的重排，不影响选区驱动的重新锚定）
        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseMove(new Point(260, 240));
        window.MouseUp(new Point(260, 240), MouseButton.Left);

        Canvas.GetLeft(window.ActionBar).ShouldNotBe(moved);

        // 位置应等于「按当前选区重新锚定」的结果（窗口窄时会钳到左边缘，所以不能断言居中）
        var selectionRect = new Rect(
            Canvas.GetLeft(window.Selection), Canvas.GetTop(window.Selection),
            window.Selection.Width, window.Selection.Height);
        var expected = CaptureWindow.ComputeToolbarPosition(
            selectionRect, window.ActionBar.Bounds.Size, new Size(window.Width, window.Height));
        Canvas.GetLeft(window.ActionBar).ShouldBe(expected.X, 0.01);
        Canvas.GetTop(window.ActionBar).ShouldBe(expected.Y, 0.01);
    }

    [AvaloniaFact]
    public void 光标_按当前工具分流()
    {
        var window = CreateWithSelection();

        window.MouseMove(new Point(200, 200));
        ReferenceEquals(window.Cursor, CaptureWindow.MoveCursorValue).ShouldBeTrue();

        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseMove(new Point(200, 200));
        ReferenceEquals(window.Cursor, CaptureWindow.CrossCursorValue).ShouldBeTrue();

        window.TextToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseMove(new Point(200, 200));
        ReferenceEquals(window.Cursor, CaptureWindow.IbeamCursorValue).ShouldBeTrue();

        window.EraserToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseMove(new Point(200, 200));
        ReferenceEquals(window.Cursor, CaptureWindow.CrossCursorValue).ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 框选按住Shift得到正方形()
    {
        var window = new CaptureWindow();
        window.Show();
        window.Width = 800;
        window.Height = 600;

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 220), RawInputModifiers.Shift);
        window.MouseUp(new Point(300, 220), MouseButton.Left);

        window.Selection.Bounds.Width.ShouldBe(window.Selection.Bounds.Height, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void Esc_取消正在画的标注_不关窗口()
    {
        var window = CreateWithSelection();
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(250, 220));
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(new Point(250, 220), MouseButton.Left);

        window.IsVisible.ShouldBeTrue();
        window.Notes.ShouldBeEmpty();

        window.Close();
    }

    [AvaloniaFact]
    public void 方向键微调选区_Shift按10像素()
    {
        var window = CreateWithSelection();
        var origin = window.Selection.Bounds.X;

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        window.Selection.Bounds.X.ShouldBe(origin + 1, 0.01);

        window.KeyPress(Key.Left, RawInputModifiers.Shift, PhysicalKey.ArrowLeft, null);
        window.Selection.Bounds.X.ShouldBe(origin - 9, 0.01);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.Selection.Bounds.X.ShouldBe(origin + 1, 0.01);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.Selection.Bounds.X.ShouldBe(origin, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 导出圆角会同步到选区预览()
    {
        var window = CreateWithSelection();

        window.CornerRadiusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.ExportCornerRadius.ShouldBe(10);
        window.Selection.RadiusX.ShouldBe(10, 0.01);
        window.Selection.RadiusY.ShouldBe(10, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 二级压缩会把翻译也收起来()
    {
        var window = new CaptureWindow();
        window.ConfigureTranslateTargets([("中文", "中文")]);
        window.TranslateButton.IsVisible.ShouldBeTrue();

        window.SetToolbarDensity(2);
        window.SaveButton.IsVisible.ShouldBeFalse();
        window.MoreButton.IsVisible.ShouldBeTrue();
        window.TranslateButton.IsVisible.ShouldBeTrue();   // 翻译是主功能，二级也不藏

        window.SetToolbarDensity(1);
        window.TranslateButton.IsVisible.ShouldBeTrue();
    }

    [Fact]
    public void 工具条位置比例_可往返()
    {
        var screen = new Rect(100, 50, 800, 600);
        var toolbar = new Size(400, 46);
        var position = new Point(300, 200);

        var fraction = CaptureWindow.RememberToolbarFraction(screen, toolbar, position);
        var restored = CaptureWindow.RestoreToolbarPosition(screen, toolbar, fraction);

        restored.X.ShouldBe(position.X, 0.01);
        restored.Y.ShouldBe(position.Y, 0.01);
        fraction.X.ShouldBeInRange(0, 1);
        fraction.Y.ShouldBeInRange(0, 1);
    }

    [AvaloniaFact]
    public void 拖动工具条会记住比例位置()
    {
        var window = CreateWithSelection();
        var origin = window.ToolbarDragHandle.TranslatePoint(new Point(6, 14), window)!.Value;

        window.MouseDown(origin, MouseButton.Left);
        window.MouseMove(new Point(origin.X + 80, origin.Y + 40));
        window.MouseUp(new Point(origin.X + 80, origin.Y + 40), MouseButton.Left);

        window.CaptureToolbarPlacement.Count.ShouldBe(2);
        window.CaptureToolbarPlacement[0].ShouldBeInRange(0, 1);
        window.CaptureToolbarPlacement[1].ShouldBeInRange(0, 1);

        window.Close();
    }

    [AvaloniaFact]
    public void 保存的工具条位置下次生效()
    {
        var window = new CaptureWindow { CaptureToolbarPlacement = [0.25, 0.75] };
        window.Show();
        window.Width = 800;
        window.Height = 600;

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        Canvas.GetLeft(window.ActionBar).ShouldBeGreaterThan(0);
        Canvas.GetTop(window.ActionBar).ShouldBeGreaterThan(0);
        window.CaptureToolbarPlacement[0].ShouldBe(0.25);

        window.Close();
    }
}
