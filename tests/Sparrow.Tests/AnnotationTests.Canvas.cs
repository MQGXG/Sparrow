// AnnotationTests 分部类：画布交互：工具切换、选区移动、绘制与预设。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shouldly;
using SkiaSharp;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

public sealed partial class AnnotationTests
{
    /// <summary>画一个矩形并切回指针工具，返回窗口。</summary>
    private static CaptureWindow CreateWithRectangle()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(250, 220));
        window.MouseUp(new Point(250, 220), MouseButton.Left);

        window.SelectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return window;
    }

    [AvaloniaFact]
    public void 删除标注后撤销能恢复()
    {
        var window = CreateWithRectangle();

        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseUp(new Point(150, 185), MouseButton.Left);
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.Notes.ShouldBeEmpty();

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.Notes.Count.ShouldBe(1);

        window.Close();
    }

    [AvaloniaFact]
    public void 橡皮擦删除后撤销能恢复()
    {
        var window = CreateWithRectangle();

        window.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, null);
        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseUp(new Point(150, 185), MouseButton.Left);
        window.Notes.ShouldBeEmpty();

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.Notes.Count.ShouldBe(1);

        window.Close();
    }

    [AvaloniaFact]
    public void 拖动标注后撤销能移回原位()
    {
        var window = CreateWithRectangle();

        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseMove(new Point(190, 215));
        window.MouseUp(new Point(190, 215), MouseButton.Left);

        var moved = window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Rect;
        moved.X.ShouldBe(90, 0.01);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rect.ShouldBe(new Rect(50, 50, 100, 70));

        window.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift,
            PhysicalKey.Z, null);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Rect.ShouldBe(moved);

        window.Close();
    }

    [AvaloniaFact]
    public void 指针工具_点中标注可以拖动移动()
    {
        var window = CreateWithRectangle();
        window.Notes.Count.ShouldBe(1);

        // 矩形的左边框在选区坐标 (50, 50..120)，窗口坐标 150
        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseMove(new Point(190, 215));
        window.MouseUp(new Point(190, 215), MouseButton.Left);

        var shape = window.Notes[0].ShouldBeOfType<ShapeAnnotation>();
        shape.Rect.X.ShouldBe(90, 0.01);
        shape.Rect.Y.ShouldBe(80, 0.01);
        shape.Rect.Width.ShouldBe(100, 0.01);
        shape.Rect.Height.ShouldBe(70, 0.01);

        // 选中态多一层虚线框
        window.NoteLayer.Children.Count.ShouldBe(2);

        window.Close();
    }

    [AvaloniaFact]
    public void 指针工具_选中后按删除键移除()
    {
        var window = CreateWithRectangle();

        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseUp(new Point(150, 185), MouseButton.Left);
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);

        window.Notes.ShouldBeEmpty();
        window.NoteLayer.IsVisible.ShouldBeFalse();

        window.Close();
    }

    [AvaloniaFact]
    public void 指针工具_点空白处取消选中()
    {
        var window = CreateWithRectangle();

        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseUp(new Point(150, 185), MouseButton.Left);
        window.NoteLayer.Children.Count.ShouldBe(2);

        // 选区内的空白（矩形中间是空心的）：只取消选中，不删标注
        window.MouseDown(new Point(200, 185), MouseButton.Left);
        window.MouseUp(new Point(200, 185), MouseButton.Left);

        window.NoteLayer.Children.Count.ShouldBe(1);
        window.Notes.Count.ShouldBe(1);

        window.Close();
    }

    [AvaloniaFact]
    public void 移动选区_标注反向平移保持屏幕位置()
    {
        var window = CreateWithRectangle();
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rect.ShouldBe(new Rect(50, 50, 100, 70));

        // 在矩形空心处按下并拖动整个选区 +50/+30
        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseMove(new Point(230, 210));
        window.MouseUp(new Point(230, 210), MouseButton.Left);

        window.Selection.Bounds.X.ShouldBe(150, 0.01);
        window.Selection.Bounds.Y.ShouldBe(130, 0.01);

        // 本地坐标反向平移，屏幕上仍在 (150,150)
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rect.ShouldBe(new Rect(0, 20, 100, 70));

        window.Close();
    }

    [AvaloniaFact]
    public void 拉伸选区左边缘_标注保持屏幕位置()
    {
        var window = CreateWithRectangle();

        // 选区左边缘中点：拖到 130，原点右移 30
        window.MouseDown(new Point(100, 200), MouseButton.Left);
        window.MouseMove(new Point(130, 200));
        window.MouseUp(new Point(130, 200), MouseButton.Left);

        window.Selection.Bounds.X.ShouldBe(130, 0.01);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rect.X.ShouldBe(20, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 重新框选后序号从1开始()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);
        window.MouseDown(new Point(220, 220), MouseButton.Left);
        window.MouseUp(new Point(220, 220), MouseButton.Left);
        window.Notes.Count.ShouldBe(2);

        // 在选区外重新拉框
        window.MouseDown(new Point(600, 450), MouseButton.Left);
        window.MouseMove(new Point(700, 550));
        window.MouseUp(new Point(700, 550), MouseButton.Left);

        // 新选区里再点序号：从 1 重新开始
        window.MouseDown(new Point(650, 500), MouseButton.Left);
        window.MouseUp(new Point(650, 500), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Number.ShouldBe(1);

        window.Close();
    }

    [AvaloniaFact]
    public void 移动选区后画新标注_撤销优先撤标注()
    {
        var window = CreateWithRectangle();

        // 先移动选区
        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseMove(new Point(230, 210));
        window.MouseUp(new Point(230, 210), MouseButton.Left);
        window.Selection.Bounds.X.ShouldBe(150, 0.01);

        // 再画一个新的矩形
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseDown(new Point(200, 160), MouseButton.Left);
        window.MouseMove(new Point(260, 200));
        window.MouseUp(new Point(260, 200), MouseButton.Left);
        window.Notes.Count.ShouldBe(2);

        // Ctrl+Z 应该先撤掉刚画的标注，选区保持在移动后的位置
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);

        window.Notes.Count.ShouldBe(1);
        window.Selection.Bounds.X.ShouldBe(150, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 点选标注不清掉选区移动的撤销()
    {
        var window = CreateWithRectangle();

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseMove(new Point(230, 210));
        window.MouseUp(new Point(230, 210), MouseButton.Left);
        window.Selection.Bounds.X.ShouldBe(150, 0.01);

        // 只是点一下标注（不拖动），不应该清掉选区移动的撤销
        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseUp(new Point(150, 185), MouseButton.Left);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);

        window.Selection.Bounds.X.ShouldBe(100, 0.01);
        window.Notes.Count.ShouldBe(1);

        window.Close();
    }

    [AvaloniaFact]
    public void 移动选区_可以撤销和重做()
    {
        var window = CreateWithRectangle();

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseMove(new Point(230, 210));
        window.MouseUp(new Point(230, 210), MouseButton.Left);

        window.Selection.Bounds.X.ShouldBe(150, 0.01);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rect.X.ShouldBe(0, 0.01);

        // 撤销移动：选区和标注一起回到原位
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);

        window.Selection.Bounds.X.ShouldBe(100, 0.01);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rect.ShouldBe(new Rect(50, 50, 100, 70));

        // 重做移动
        window.KeyPress(
            Key.Z,
            RawInputModifiers.Control | RawInputModifiers.Shift,
            PhysicalKey.Z,
            null);

        window.Selection.Bounds.X.ShouldBe(150, 0.01);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rect.X.ShouldBe(0, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_切换工具会取消选中()
    {
        var window = new CaptureWindow();

        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.RectToolButton.Classes.Contains("selected").ShouldBeTrue();

        // 再点一次同一工具：回到无工具状态
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.RectToolButton.Classes.Contains("selected").ShouldBeFalse();
    }

    [Fact]
    public void 马赛克_像素化把棋盘变成灰块()
    {
        var png = CreateCheckerPng(120, 120);
        var item = new MosaicAnnotation(new Rect(40, 40, 40, 40), 8, Blur: false);

        var composed = AnnotationRenderer.Compose(png, [item], 1);

        using var decoded = SKBitmap.Decode(composed);
        var pixel = decoded.GetPixel(60, 60);
        // 棋盘格被平均成灰：既不是纯黑也不是纯白
        ((int)pixel.Red).ShouldBeGreaterThan(40);
        ((int)pixel.Red).ShouldBeLessThan(220);
    }

    [Fact]
    public void 文字和序号_会在图上留下像素()
    {
        var png = CreateWhitePng(120, 120);
        var items = new IAnnotation[]
        {
            new TextAnnotation(new Point(10, 40), "Hi", 0xFFE5A44C, 24),
            new NumberAnnotation(new Point(80, 80), 3, 0xFFE5A44C, 24)
        };

        var composed = AnnotationRenderer.Compose(png, items, 1);
        using var decoded = SKBitmap.Decode(composed);

        HasInk(decoded, 8, 10, 60, 45).ShouldBeTrue("文字应该画出笔画");

        var badge = decoded.GetPixel(70, 80);
        ((int)badge.Blue).ShouldBeLessThan(150);   // 琥珀圆底
    }

    [AvaloniaFact]
    public void 截图窗口_马赛克工具拖拽后有预览()
    {
        var window = CreateWindowWithSelection();
        window.MosaicToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(160, 160), MouseButton.Left);
        window.MouseMove(new Point(260, 240));
        window.MouseUp(new Point(260, 240), MouseButton.Left);

        window.NoteLayer.Children.Count.ShouldBe(1);
    }

    [AvaloniaFact]
    public void 截图窗口_序号工具点击递增且可撤销()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);
        window.MouseDown(new Point(220, 220), MouseButton.Left);
        window.MouseUp(new Point(220, 220), MouseButton.Left);

        // 两个序号 = 两个圆 + 两个数字
        window.NoteLayer.Children.Count.ShouldBe(4);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.NoteLayer.Children.Count.ShouldBe(2);
    }

    [AvaloniaFact]
    public void 截图窗口_文字工具点击出现输入框()
    {
        var window = CreateWindowWithSelection();
        window.TextToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(170, 170), MouseButton.Left);
        window.MouseUp(new Point(170, 170), MouseButton.Left);

        window.TextNoteBox.IsVisible.ShouldBeTrue();
        window.NoteLayer.Children.Count.ShouldBe(0);
    }

    [AvaloniaFact]
    public void 截图窗口_颜色切换影响之后画的图元()
    {
        var window = CreateWindowWithSelection();
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // 换成红色再画
        window.ColorRedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(250, 220));
        window.MouseUp(new Point(250, 220), MouseButton.Left);

        var rectangle = window.NoteLayer.Children[0].ShouldBeOfType<Avalonia.Controls.Shapes.Rectangle>();
        var brush = rectangle.Stroke.ShouldBeOfType<Avalonia.Media.SolidColorBrush>();
        brush.Color.ShouldBe(Avalonia.Media.Color.Parse("#FF5F5F"));
    }

    [AvaloniaFact]
    public void 截图窗口_数字键切换工具()
    {
        var window = CreateWindowWithSelection();

        window.KeyPress(Key.D4, RawInputModifiers.None, PhysicalKey.Digit4, null);
        window.ArrowToolButton.Classes.Contains("selected").ShouldBeTrue();

        window.KeyPress(Key.D7, RawInputModifiers.None, PhysicalKey.Digit7, null);
        window.MosaicToolButton.Classes.Contains("selected").ShouldBeTrue();
        window.ArrowToolButton.Classes.Contains("selected").ShouldBeFalse();
    }

    [AvaloniaFact]
    public void 截图窗口_马赛克模式可切换像素与模糊()
    {
        var window = new CaptureWindow();

        // 属性栏用分段按钮显示当前模式
        window.MosaicPixelButton.Classes.Contains("selected").ShouldBeTrue();
        window.MosaicBlurButton.Classes.Contains("selected").ShouldBeFalse();

        window.ToggleMosaicBlur(true);
        window.MosaicBlurButton.Classes.Contains("selected").ShouldBeTrue();
        window.MosaicPixelButton.Classes.Contains("selected").ShouldBeFalse();

        window.ToggleMosaicBlur(false);
        window.MosaicPixelButton.Classes.Contains("selected").ShouldBeTrue();
    }

    [AvaloniaFact]
    public void 属性条_线型与字号改用分段按钮()
    {
        var window = new CaptureWindow();

        // 线型：默认实线
        window.DashSolidButton.Classes.Contains("selected").ShouldBeTrue();
        window.DashDashButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.DashDashButton.Classes.Contains("selected").ShouldBeTrue();
        window.DashSolidButton.Classes.Contains("selected").ShouldBeFalse();

        // 字号：默认 16 对应 M
        window.FontMButton.Classes.Contains("selected").ShouldBeTrue();
        window.FontXlButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.FontXlButton.Classes.Contains("selected").ShouldBeTrue();
        window.FontMButton.Classes.Contains("selected").ShouldBeFalse();
    }

    [AvaloniaFact]
    public void 截图窗口_样式行只在选中工具时出现()
    {
        var window = new CaptureWindow();

        window.StyleRow.IsVisible.ShouldBeFalse("没选工具时属性行不该占位置");

        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.StyleRow.IsVisible.ShouldBeTrue();

        // 再点一次同一工具：回到无工具状态，属性行收起
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.StyleRow.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void 圆角导出_四角透明中间保留()
    {
        var png = CreateWhitePng(100, 100);

        var rounded = AnnotationRenderer.RoundCorners(png, 24);

        using var decoded = SKBitmap.Decode(rounded);
        decoded.Width.ShouldBe(100);

        ((int)decoded.GetPixel(2, 2).Alpha).ShouldBe(0);
        ((int)decoded.GetPixel(50, 50).Alpha).ShouldBe(255);
    }

    [AvaloniaFact]
    public void 截图窗口_圆角开关切换导出半径()
    {
        var window = new CaptureWindow();
        window.ExportCornerRadius.ShouldBe(0);

        window.CornerRadiusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.ExportCornerRadius.ShouldBe(10);
        window.CornerRadiusButton.Classes.Contains("selected").ShouldBeTrue();

        window.CornerRadiusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.ExportCornerRadius.ShouldBe(0);
    }

    [AvaloniaFact]
    public void 截图窗口_样式模板可保存应用清除()
    {
        var window = new CaptureWindow();

        window.NoteColorValue = 0xFFFF5F5F;
        window.NoteStrokeWidth = 8;
        window.NoteMosaicBlur = true;
        window.ExportCornerRadius = 10;
        window.SavePreset(0);

        window.Presets[0].ShouldNotBeNull();
        window.Presets[0]!.Color.ShouldBe("#FF5F5F");

        // 改回默认，再应用模板
        window.NoteColorValue = 0xFFE5A44C;
        window.NoteStrokeWidth = 3;
        window.NoteMosaicBlur = false;
        window.ExportCornerRadius = 0;

        window.ApplyPreset(0);

        window.NoteColorValue.ShouldBe(0xFFFF5F5F);
        window.NoteStrokeWidth.ShouldBe(8);
        window.NoteMosaicBlur.ShouldBeTrue();
        window.ExportCornerRadius.ShouldBe(10);

        window.ClearPreset(0);
        window.Presets[0].ShouldBeNull();
    }
}
