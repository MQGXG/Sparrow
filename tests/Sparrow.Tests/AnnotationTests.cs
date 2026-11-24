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

/// <summary>截图标注：图元撤销、箭头几何、Skia 合成，以及窗口里的画图交互。</summary>
public sealed class AnnotationTests
{
    [Fact]
    public void 撤销_按顺序回退并可重做()
    {
        var document = new AnnotationDocument();
        var first = new ShapeAnnotation(new Rect(0, 0, 10, 10), 0xFFFF0000, 2, false);
        var second = new ArrowAnnotation(new Point(0, 0), new Point(10, 10), 0xFFFF0000, 2);

        document.Add(first);
        document.Add(second);
        document.CanUndo.ShouldBeTrue();

        document.Undo();
        document.Items.Count.ShouldBe(1);
        document.Items[0].ShouldBe(first);

        document.Redo();
        document.Items.Count.ShouldBe(2);

        // 撤销后画新的：重做栈要清空
        document.Undo();
        document.Add(second);
        document.CanRedo.ShouldBeFalse();

        document.Clear();
        document.Items.ShouldBeEmpty();
        document.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void 合成_旋转矩形按中心旋转()
    {
        var png = CreateWhitePng(100, 100);
        var shape = new ShapeAnnotation(
            new Rect(20, 20, 60, 20), 0xFFFF0000, 2, false, Rotation: 90);

        using var decoded = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [shape], 1));

        // 绕中心旋转 90° 后变成 x=40..60、y=0..60 的竖条
        decoded.GetPixel(60, 30).Green.ShouldBeLessThan((byte)120);
        decoded.GetPixel(20, 30).Green.ShouldBeGreaterThan((byte)200);
    }

    [Fact]
    public void 缩放标注_矩形和点按包围盒映射()
    {
        var from = new Rect(10, 10, 100, 50);
        var to = new Rect(20, 20, 200, 100);

        var shape = new ShapeAnnotation(from, 0xFFFF0000, 2, false);
        var scaledShape = AnnotationGeometry.Scale(shape, from, to)
            .ShouldBeOfType<ShapeAnnotation>();
        scaledShape.Rect.ShouldBe(to);

        var arrow = new ArrowAnnotation(new Point(10, 10), new Point(110, 60), 0xFFFF0000, 2);
        var scaledArrow = AnnotationGeometry.Scale(arrow, from, to)
            .ShouldBeOfType<ArrowAnnotation>();
        scaledArrow.From.ShouldBe(new Point(20, 20));
        scaledArrow.To.ShouldBe(new Point(220, 120));

        AnnotationGeometry.IsScalable(new TextAnnotation(
            new Point(0, 0), "x", 0xFFFF0000, 16)).ShouldBeTrue();

        var text = new TextAnnotation(new Point(10, 10), "x", 0xFFFF0000, 16);
        var scaledText = AnnotationGeometry.Scale(text, from, to)
            .ShouldBeOfType<TextAnnotation>();
        scaledText.FontSize.ShouldBe(32, 0.01);
        scaledText.Origin.ShouldBe(new Point(20, 20));
    }

    [AvaloniaFact]
    public void 拖动旋转手柄旋转标注并可撤销()
    {
        var window = CreateWithRectangle();

        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseUp(new Point(150, 185), MouseButton.Left);

        // 标注本地包围盒 (50,50,100,70)，中心 (100,85)，旋转柄在 (100,32)
        // 屏幕上分别是 (200,185) 和 (200,132)
        window.MouseDown(new Point(200, 132), MouseButton.Left);
        // 目标向量从 (0,-53) 转到 (53,0)：旋转 90°
        window.MouseMove(new Point(253, 185));
        window.MouseUp(new Point(253, 185), MouseButton.Left);

        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rotation.ShouldBe(90, 1);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rotation.ShouldBe(0, 1);

        window.Close();
    }

    [AvaloniaFact]
    public void 选中标注显示缩放点_拖右下角放大并可撤销()
    {
        var window = CreateWithRectangle();

        // 点中矩形边框：出现 8 个缩放点
        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseUp(new Point(150, 185), MouseButton.Left);

        window.NoteHandleLayer.IsVisible.ShouldBeTrue();
        window.NoteHandleLayer.Children.Count.ShouldBeGreaterThanOrEqualTo(8);

        // 右下角控制点在 (250,220)，拖到 (300,250)
        window.MouseDown(new Point(250, 220), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        var rect = window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Rect;
        rect.Width.ShouldBe(150, 0.01);
        rect.Height.ShouldBe(100, 0.01);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>()
            .Rect.ShouldBe(new Rect(50, 50, 100, 70));

        window.Close();
    }

    [Fact]
    public void 撤销_删除记录原索引并可重做()
    {
        var document = new AnnotationDocument();
        var first = new ShapeAnnotation(new Rect(0, 0, 10, 10), 0xFFFF0000, 2, false);
        var second = new ArrowAnnotation(new Point(0, 0), new Point(10, 10), 0xFFFF0000, 2);
        var third = new TextAnnotation(new Point(0, 0), "x", 0xFFFF0000, 16);

        document.Add(first);
        document.Add(second);
        document.Add(third);

        document.RemoveAt(1);
        document.Items.Count.ShouldBe(2);
        document.Items[1].ShouldBe(third);

        document.Undo();
        document.Items.Count.ShouldBe(3);
        document.Items[1].ShouldBe(second);

        document.Redo();
        document.Items.Count.ShouldBe(2);
        document.Items[1].ShouldBe(third);
    }

    [Fact]
    public void 撤销_替换可恢复到旧内容()
    {
        var document = new AnnotationDocument();
        var before = new TextAnnotation(new Point(0, 0), "A", 0xFFFF0000, 16);
        document.Add(before);

        var after = before with { Text = "B" };
        document.ReplaceWithHistory(0, after);
        document.Items[0].ShouldBe(after);

        document.Undo();
        document.Items[0].ShouldBe(before);

        document.Redo();
        document.Items[0].ShouldBe(after);
    }

    [Fact]
    public void 箭头头部_以终点为顶点并关于轴线对称()
    {
        var from = new Point(0, 0);
        var to = new Point(100, 0);
        var (left, right) = ArrowGeometry.Head(from, to, 12);

        // 两翼都在终点左侧（指回起点方向）
        left.X.ShouldBeLessThan(to.X);
        right.X.ShouldBeLessThan(to.X);

        // 关于 y = 0 对称
        left.Y.ShouldBe(-right.Y, tolerance: 0.001);
        left.X.ShouldBe(right.X, tolerance: 0.001);

        // 零长度不给 NaN
        var (sameLeft, sameRight) = ArrowGeometry.Head(to, to, 12);
        sameLeft.ShouldBe(to);
        sameRight.ShouldBe(to);
    }

    [Fact]
    public void 合成_放大镜把中心区域按倍数放大()
    {
        const int width = 100, height = 100;
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var dot = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(14, 14, 1, 1, dot);
        }

        byte[] png;
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            png = data.ToArray();

        // 镜片 (8,8) 16×16，放大 4 倍：源区域是中心 4×4 = (14,14)-(18,18)
        var magnifier = new MagnifierAnnotation(new Rect(8, 8, 16, 16), Zoom: 4);
        var composed = AnnotationRenderer.Compose(png, [magnifier], scale: 1);

        using var decoded = SKBitmap.Decode(composed);

        // 源区域左上角那颗黑点被放大成 4×4，落在镜片左上角
        ((int)decoded.GetPixel(9, 9).Red).ShouldBeLessThan(120);
        // 镜片右下角对应源的右下角，仍是白底
        ((int)decoded.GetPixel(20, 20).Red).ShouldBeGreaterThan(180);
    }

    [Fact]
    public void 合成_聚光灯只压暗区域外()
    {
        var png = CreateWhitePng(100, 100);
        var spotlight = new SpotlightAnnotation(
            new Rect(30, 30, 40, 40), 0xFF000000, 1, Ellipse: true);

        var composed = AnnotationRenderer.Compose(png, [spotlight], scale: 1);

        using var decoded = SKBitmap.Decode(composed);

        // 洞外被压暗
        ((int)decoded.GetPixel(5, 5).Red).ShouldBeLessThan(50);
        // 洞里保持原样
        ((int)decoded.GetPixel(50, 50).Red).ShouldBeGreaterThan(200);
    }

    [Fact]
    public void 合成_直线只画线身_没有箭头()
    {
        var png = CreateWhitePng(100, 100);
        var line = new LineAnnotation(
            new Point(10, 50), new Point(90, 50), 0xFF0000FF, 2, 1, LineDash.Solid);

        var composed = AnnotationRenderer.Compose(png, [line], scale: 1);

        using var decoded = SKBitmap.Decode(composed);

        // 线身中点是蓝色
        var middle = decoded.GetPixel(50, 50);
        ((int)middle.Blue).ShouldBeGreaterThan(180);
        ((int)middle.Red).ShouldBeLessThan(120);

        // 终点附近不该有箭头那样的“翼”：端点右侧一点仍是白底
        var beyond = decoded.GetPixel(95, 50);
        ((int)beyond.Red).ShouldBeGreaterThan(200);
    }

    [AvaloniaFact]
    public void 橡皮擦_点中标注就删掉()
    {
        var window = new CaptureWindow();
        window.Show();
        window.Width = 800;
        window.Height = 600;

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        // 画一个矩形
        window.KeyPress(Key.D2, RawInputModifiers.None, PhysicalKey.Digit2, null);
        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(250, 220));
        window.MouseUp(new Point(250, 220), MouseButton.Left);
        window.Notes.Count.ShouldBe(1);

        // 橡皮擦点它的边框
        window.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, null);
        window.MouseDown(new Point(150, 185), MouseButton.Left);
        window.MouseUp(new Point(150, 185), MouseButton.Left);

        window.Notes.Count.ShouldBe(0);

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_直线工具拖出LineAnnotation_且可命中()
    {
        var window = new CaptureWindow();
        window.Show();
        window.Width = 800;
        window.Height = 600;

        // 先框一个选区
        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        window.KeyPress(Key.L, RawInputModifiers.None, PhysicalKey.L, null);
        window.LineToolButton.Classes.Contains("selected").ShouldBeTrue();

        // 在选区内拖一条直线
        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<LineAnnotation>();

        window.Close();
    }

    [Fact]
    public void 合成_把标注画进图片并保持尺寸()
    {
        var png = CreateWhitePng(100, 100);
        var shape = new ShapeAnnotation(new Rect(20, 20, 40, 40), 0xFFFF0000, 2, false);

        var composed = AnnotationRenderer.Compose(png, [shape], scale: 1);

        using var decoded = SKBitmap.Decode(composed);
        decoded.Width.ShouldBe(100);
        decoded.Height.ShouldBe(100);

        // 上边中点应该被画成红色
        var pixel = decoded.GetPixel(40, 20);
        ((int)pixel.Red).ShouldBeGreaterThan(180);
        ((int)pixel.Green).ShouldBeLessThan(120);
    }

    [Fact]
    public void 合成_按缩放把DIP映射到物理像素()
    {
        var png = CreateWhitePng(200, 200);
        // DIP 20..60，2 倍缩放后应该落在物理 40..120
        var shape = new ShapeAnnotation(new Rect(20, 20, 40, 40), 0xFF0000FF, 2, false);

        var composed = AnnotationRenderer.Compose(png, [shape], scale: 2);

        using var decoded = SKBitmap.Decode(composed);
        ((int)decoded.GetPixel(80, 40).Blue).ShouldBeGreaterThan(150);   // 上边中点
        ((int)decoded.GetPixel(20, 20).Red).ShouldBeGreaterThan(200);    // 左上角仍是白底
    }

    [Fact]
    public void 没有标注时_原样返回()
    {
        var png = CreateWhitePng(10, 10);
        AnnotationRenderer.Compose(png, [], 1).ShouldBeSameAs(png);
    }

    [AvaloniaFact]
    public void 截图窗口_选矩形工具拖拽后出现标注并可撤销()
    {
        var window = new CaptureWindow();
        window.Show();

        // 先拖出选区
        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        // 切到矩形工具
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.RectToolButton.Classes.Contains("selected").ShouldBeTrue();

        // 在选区内画一个矩形
        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(250, 220));
        window.MouseUp(new Point(250, 220), MouseButton.Left);

        window.NoteLayer.IsVisible.ShouldBeTrue();
        window.NoteLayer.Children.Count.ShouldBe(1);

        // Ctrl+Z 撤掉
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, null);
        window.NoteLayer.Children.Count.ShouldBe(0);
    }

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

    private static CaptureWindow CreateWindowWithSelection()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        return window;
    }

    private static bool HasInk(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            if (!IsWhite(bitmap.GetPixel(x, y)))
                return true;
        }

        return false;
    }

    private static bool IsWhite(SKColor color) =>
        color.Red == 255 && color.Green == 255 && color.Blue == 255;

    private static byte[] CreateCheckerPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            bitmap.SetPixel(x, y, (x + y) % 2 == 0 ? SKColors.Black : SKColors.White);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateWhitePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.White);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void 形状标注_填充与圆角进导出()
    {
        var png = CreateWhitePng(120, 120);

        var shape = new ShapeAnnotation(
            new Rect(10, 10, 100, 100),
            0xFFFF0000,
            2,
            false,
            Opacity: 1,
            Fill: ShapeFill.Soft,
            Radius: 30);

        var result = AnnotationRenderer.Compose(png, [shape], 1);
        using var decoded = SKBitmap.Decode(result);

        // 圆角切掉的位置保持原图白色
        decoded.GetPixel(12, 12).Red.ShouldBe((byte)255);
        decoded.GetPixel(12, 12).Green.ShouldBe((byte)255);

        // 内部被淡红覆盖
        var center = decoded.GetPixel(60, 60);
        center.Red.ShouldBe((byte)255);
        center.Green.ShouldBeLessThan((byte)240);
    }

    [Fact]
    public void 形状标注_透明度越低覆盖越浅()
    {
        var png = CreateWhitePng(80, 80);

        var solid = new ShapeAnnotation(
            new Rect(10, 10, 60, 60),
            0xFFFF0000,
            2,
            false,
            Opacity: 1,
            Fill: ShapeFill.Soft);

        var faint = solid with { Opacity = 0.3 };

        using var strong = SKBitmap.Decode(AnnotationRenderer.Compose(png, [solid], 1));
        using var light = SKBitmap.Decode(AnnotationRenderer.Compose(png, [faint], 1));

        strong.GetPixel(40, 40).Green.ShouldBeLessThan(light.GetPixel(40, 40).Green);
    }

    [Fact]
    public void 笔触_手绘和涂鸦会偏离直线轴线()
    {
        var png = CreateWhitePng(120, 60);
        var solid = new LineAnnotation(
            new Point(10, 30), new Point(110, 30), 0xFFFF0000, 2);

        int TopInk(SKBitmap bitmap)
        {
            var top = int.MaxValue;
            for (var x = 12; x < 108; x++)
            {
                for (var y = 0; y < 60; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.Red > 180 && pixel.Green < 160)
                    {
                        if (y < top)
                            top = y;
                        break;
                    }
                }
            }

            return top;
        }

        using var solidImage = SKBitmap.Decode(AnnotationRenderer.Compose(png, [solid], 1));
        using var waveImage = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [solid with { Style = LineStyle.Sketch }], 1));
        using var zigzagImage = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [solid with { Style = LineStyle.Rough }], 1));

        var solidTop = TopInk(solidImage);
        solidTop.ShouldBeLessThan(int.MaxValue);

        // 手绘/涂鸦沿法线鼓出，最高点会比直线更靠上
        var sketchTop = TopInk(waveImage);
        var roughTop = TopInk(zigzagImage);
        sketchTop.ShouldBeLessThan(solidTop);
        roughTop.ShouldBeLessThan(solidTop);

        // 涂鸦抖动更大，而且是双线，墨迹比手绘多
        roughTop.ShouldBeLessThan(sketchTop);
        Ink(zigzagImage).ShouldBeGreaterThan(Ink(waveImage));
    }

    private static int Ink(SKBitmap bitmap)
    {
        var count = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 180 && pixel.Green < 160)
                    count++;
            }
        }

        return count;
    }

    [AvaloniaFact]
    public void 选中标注后_属性栏修改直接作用于该标注()
    {
        var window = new CaptureWindow();
        window.Show();

        // 拉选区 + 画一个矩形
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);
        window.Notes.Count.ShouldBe(1);

        // 选中该矩形：属性栏的线型/线宽/填充应直接改这条标注
        window.SelectNote(0);

        window.StyleSketchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Style.ShouldBe(LineStyle.Sketch);

        window.Width8Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Width.ShouldBe(8);

        window.FillLineButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Fill.ShouldBe(ShapeFill.Line);

        window.ColorRedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Color.ShouldBe(0xFFFF5F5F);

        window.Close();
    }

    [AvaloniaFact]
    public void 线型组在调色板中_不再占属性栏一行()
    {
        var window = new CaptureWindow();
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.CustomColorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.CustomColorPanel.IsVisible.ShouldBeTrue();
        window.DashGroup.IsVisible.ShouldBeTrue();
        window.StyleGroups.Children.Contains(window.DashGroup).ShouldBeFalse();

        window.Close();
    }

    [AvaloniaFact]
    public void 描边选项互斥_选笔触会覆盖线型()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        window.SelectNote(0);

        window.DashDashButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dashed = window.Notes[0].ShouldBeOfType<ShapeAnnotation>();
        dashed.Dash.ShouldBe(LineDash.Dash);
        dashed.Style.ShouldBe(LineStyle.Neat);

        // 选「涂鸦」= 实线 + 涂鸦笔触，不会和虚线叠加
        window.StyleRoughButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var rough = window.Notes[0].ShouldBeOfType<ShapeAnnotation>();
        rough.Style.ShouldBe(LineStyle.Rough);
        rough.Dash.ShouldBe(LineDash.Solid);

        // 回到虚线，笔触也回到规整
        window.DashDashButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var back = window.Notes[0].ShouldBeOfType<ShapeAnnotation>();
        back.Dash.ShouldBe(LineDash.Dash);
        back.Style.ShouldBe(LineStyle.Neat);

        window.Close();
    }

    [AvaloniaFact]
    public void 序号_外形直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.SelectNote(0);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Shape.ShouldBe(NumberShape.SolidCircle);

        // 实心圆预览 = 圆 + 文字（2 个子元素）
        var circleChildren = window.NoteLayer.Children.Count;

        window.SetNumberShape(NumberShape.Pin);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Shape.ShouldBe(NumberShape.Pin);
        window.NumberShapeButton.Content.ShouldBe("定位针");

        // 定位针预览 = 圆头 + 尖三角 + 文字（3 个子元素）
        window.NoteLayer.Children.Count.ShouldBeGreaterThan(circleChildren);

        window.SetNumberShape(NumberShape.Hexagon);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Shape.ShouldBe(NumberShape.Hexagon);
        window.NumberShapeButton.Content.ShouldBe("六边形");

        window.Close();
    }

    [Fact]
    public void 序号组合_详情区按目标点推导_显式几何优先()
    {
        var note = new NumberAnnotation(new Point(0, 0), 1, 0xFFFF0000, 24,
            Combo: NumberCombo.DashedBox, Target: new Point(100, 50));

        // 详情区 = badge 中心 → 目标点的包围盒
        var badge = NumberGeometry.Bounds(note);
        var detail = NumberGeometry.DetailBounds(note);
        detail.Left.ShouldBe(badge.Center.X);
        detail.Top.ShouldBe(badge.Center.Y);
        detail.Right.ShouldBe(100);
        detail.Bottom.ShouldBe(50);

        // 拖过之后用显式几何，不再推导
        var fixedBox = new Rect(10, 20, 30, 40);
        NumberGeometry.DetailBounds(note with { Detail = fixedBox }).ShouldBe(fixedBox);
    }

    [AvaloniaFact]
    public void 序号_组合详情区直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.SelectNote(0);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Combo.ShouldBe(NumberCombo.Plain);

        window.SetNumberCombo(NumberCombo.DashedBox);
        var dashed = window.Notes[0].ShouldBeOfType<NumberAnnotation>();
        dashed.Combo.ShouldBe(NumberCombo.DashedBox);
        window.NumberComboButton.Content.ShouldBe("虚线框");

        // 详情区从徽章中心往右下撑开
        NumberGeometry.DetailBounds(dashed).Right
            .ShouldBeGreaterThan(NumberGeometry.Bounds(dashed).Center.X);

        window.SetNumberCombo(NumberCombo.Highlight);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Combo.ShouldBe(NumberCombo.Highlight);
        window.NumberComboButton.Content.ShouldBe("高亮区域");

        window.SetNumberCombo(NumberCombo.Plain);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Combo.ShouldBe(NumberCombo.Plain);
        window.NumberComboButton.Content.ShouldBe("仅序号");

        window.Close();
    }

    [AvaloniaFact]
    public void 序号_画完不选中也能直接换外形()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        // 故意不调用 SelectNote：模拟「刚画完直接改外形」
        window.SetNumberShape(NumberShape.Capsule);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Shape.ShouldBe(NumberShape.Capsule);

        window.SetNumberFormat(NumberFormat.Chinese);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Format.ShouldBe(NumberFormat.Chinese);

        window.Close();
    }

    [Fact]
    public void 序号组合_引导线四点()
    {
        var note = new NumberAnnotation(new Point(0, 0), 1, 0xFFFF0000, 24,
            Combo: NumberCombo.Leader, Target: new Point(120, 60));

        var points = NumberGeometry.LeaderPoints(note);
        points.Count.ShouldBe(4);

        // badge → 近肘（水平）→ 远肘（目标高度）→ 目标
        points[1].Y.ShouldBe(points[0].Y, 0.01);
        points[2].Y.ShouldBe(60, 0.01);
        points[3].ShouldBe(new Point(120, 60));
    }

    [AvaloniaFact]
    public void 序号_拖拽创建时目标点跟随指针()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // 按下放 badge，拖到右下定点，松手提交
        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        var note = window.Notes[0].ShouldBeOfType<NumberAnnotation>();

        // 选区原点是 (100,100)：徽章落在 (80,80)，目标点落在 (200,150)
        note.Center.ShouldBe(new Point(80, 80));
        note.Target.ShouldNotBeNull();
        note.Target!.Value.X.ShouldBe(200, 0.5);
        note.Target!.Value.Y.ShouldBe(150, 0.5);
        note.Number.ShouldBe(1);

        window.Close();
    }

    [Fact]
    public void 序号组合_文本标签与放大说明的详情区()
    {
        var target = new Point(200, 100);

        var text = new NumberAnnotation(new Point(0, 0), 1, 0xFFFF0000, 24,
            Combo: NumberCombo.Text, Target: target);

        var textBox = NumberGeometry.DetailBounds(text);
        textBox.Left.ShouldBeGreaterThan(NumberGeometry.Bounds(text).Right);   // 贴在徽章右侧

        var magnify = text with { Combo = NumberCombo.Magnify };
        var lens = NumberGeometry.DetailBounds(magnify);
        lens.Center.X.ShouldBe(target.X, 0.01);
        lens.Center.Y.ShouldBe(target.Y, 0.01);
        lens.Width.ShouldBe(24 * 2.5, 0.01);   // 半径 size*1.25
    }

    [AvaloniaFact]
    public void 序号_文本标签说明直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.SelectNote(0);
        window.SetNumberCombo(NumberCombo.Text);
        window.NumberTextGroup.IsVisible.ShouldBeTrue();

        window.NumberTextBox.Text = "注意这里";
        window.CommitNumberText();

        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Text.ShouldBe("注意这里");

        window.Close();
    }

    [Fact]
    public void 序号徽章_自动数字保持正圆_只有标签才撑宽()
    {
        var auto = new NumberAnnotation(new Point(0, 0), 8, 0xFFFF0000, 24);

        var badge = NumberGeometry.Bounds(auto);
        badge.Width.ShouldBe(24, 0.01);
        badge.Height.ShouldBe(24, 0.01);

        var labeled = auto with { Label = "第一步" };
        NumberGeometry.Bounds(labeled).Width.ShouldBeGreaterThan(24);
        NumberGeometry.Bounds(labeled).Height.ShouldBe(24, 0.01);
    }

    [AvaloniaFact]
    public void 序号_文本字号与放大倍率直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.SelectNote(0);

        window.SetNumberCombo(NumberCombo.Text);
        window.SetNumberTextSize(24);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().TextSize.ShouldBe(24);
        window.NumberTextSizeButton.Content.ShouldBe("24 px");

        window.SetNumberCombo(NumberCombo.Magnify);
        window.NumberZoomGroup.IsVisible.ShouldBeTrue();
        window.SetNumberZoom(4);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Zoom.ShouldBe(4);
        window.NumberZoomButton.Content.ShouldBe("4×");

        window.Close();
    }

    [AvaloniaFact]
    public void 序号_文本组合画完自动进入说明编辑()
    {
        var window = CreateWindowWithSelection();
        window.SetNumberCombo(NumberCombo.Text);
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        // 画完直接进入就地编辑
        window.TextNoteBox.IsVisible.ShouldBeTrue();

        window.TextNoteBox.Text = "注意";
        window.CommitTextNote();

        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Text.ShouldBe("注意");

        window.Close();
    }

    [AvaloniaFact]
    public void 序号_拖目标点把手改变详情区()
    {
        var window = CreateWindowWithSelection();
        window.SetNumberCombo(NumberCombo.DashedBox);
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        window.Notes[0].ShouldBeOfType<NumberAnnotation>()
            .Target!.Value.X.ShouldBe(200, 0.5);   // 选区原点 (100,100)

        // 切到指针工具，选中后拖目标点（窗口坐标 300,250 = 局部 200,150）
        window.SelectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.SelectNote(0);

        window.MouseDown(new Point(300, 250), MouseButton.Left);
        window.MouseMove(new Point(360, 280));
        window.MouseUp(new Point(360, 280), MouseButton.Left);

        var moved = window.Notes[0].ShouldBeOfType<NumberAnnotation>();
        moved.Target!.Value.X.ShouldBe(260, 0.5);
        moved.Target!.Value.Y.ShouldBe(180, 0.5);

        window.Close();
    }

    [AvaloniaFact]
    public void 调色板_取色光标从左上角定位()
    {
        var window = new CaptureWindow();
        window.Show();

        window.CustomColorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.CustomColorPanel.IsVisible.ShouldBeTrue();

        // Margin 是相对左上角的偏移；如果对齐是默认 Stretch，光标会从居中位置再偏移，和鼠标对不上
        window.SvCursor.HorizontalAlignment.ShouldBe(Avalonia.Layout.HorizontalAlignment.Left);
        window.SvCursor.VerticalAlignment.ShouldBe(Avalonia.Layout.VerticalAlignment.Top);

        window.Close();
    }

    [AvaloniaFact]
    public void 手绘描边_实时预览也画抖动路径()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        // 先选「手绘」，再画矩形
        window.SetLineStroke(LineDash.Solid, LineStyle.Sketch);
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Style.ShouldBe(LineStyle.Sketch);

        // 预览层应该是一条手绘 Path，而不是规整的 Rectangle
        window.NoteLayer.Children.Any(child => child is Avalonia.Controls.Shapes.Path)
            .ShouldBeTrue();
        window.NoteLayer.Children.Any(child => child is Avalonia.Controls.Shapes.Rectangle)
            .ShouldBeFalse();

        window.Close();
    }

    [Fact]
    public void 序号外形_顶点与文字锚点()
    {
        var bounds = new Rect(0, 0, 40, 40);

        // 圆 / 方族没有顶点，各自有画法
        NumberGeometry.Polygon(NumberShape.SolidCircle, bounds).ShouldBeNull();
        NumberGeometry.Polygon(NumberShape.Capsule, bounds).ShouldBeNull();

        // 六边形尖顶朝上
        var hexagon = NumberGeometry.Polygon(NumberShape.Hexagon, bounds)!;
        hexagon.Count.ShouldBe(6);
        hexagon[0].X.ShouldBe(20, 0.01);
        hexagon[0].Y.ShouldBe(0, 0.01);

        // 旗标 5 点、底边中央有 V 形缺口；角标是右上三角
        var flag = NumberGeometry.Polygon(NumberShape.Flag, bounds)!;
        flag.Count.ShouldBe(5);
        flag[3].Y.ShouldBe(30, 0.01);   // bottom - r*0.5 = 40 - 20*0.5

        NumberGeometry.Polygon(NumberShape.Corner, bounds)!.Count.ShouldBe(3);

        // 胶囊更宽、定位针更高
        NumberGeometry.BadgeBounds(new Point(0, 0), 32, NumberShape.Capsule).Width.ShouldBe(48, 0.01);
        NumberGeometry.BadgeBounds(new Point(0, 0), 32, NumberShape.Pin).Height.ShouldBe(41.6, 0.01);

        // 定位针的文字在圆头里；角标文字靠右上
        var pin = NumberGeometry.BadgeBounds(new Point(0, 0), 32, NumberShape.Pin);
        NumberGeometry.TextAnchor(pin, NumberShape.Pin).Y.ShouldBe(pin.Top + 16, 0.01);
        NumberGeometry.TextAnchor(bounds, NumberShape.Corner).X
            .ShouldBeGreaterThan(bounds.Center.X);
    }

    [Fact]
    public void 形状标注_斜线与交叉斜线填充换用独立填充色()
    {
        var png = CreateWhitePng(120, 120);

        ShapeAnnotation Make(ShapeFill fill) => new(
            new Rect(10, 10, 100, 100),
            0xFFFF0000,   // 红色描边
            2,
            false,
            Opacity: 1,
            Fill: fill,
            FillColor: 0xFF0000FF);   // 蓝色填充

        int BluePixels(SKBitmap bitmap)
        {
            var count = 0;
            for (var x = 20; x < 100; x++)
            {
                for (var y = 20; y < 100; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.Blue > 180 && pixel.Red < 140)
                        count++;
                }
            }

            return count;
        }

        using var line = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [Make(ShapeFill.Line)], 1));
        using var cross = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [Make(ShapeFill.CrossLine)], 1));

        var linePixels = BluePixels(line);
        linePixels.ShouldBeGreaterThan(0);

        // 交叉斜线比单向斜线多一组线，蓝色像素更多
        BluePixels(cross).ShouldBeGreaterThan(linePixels);
    }

    [AvaloniaFact]
    public void 序号_类型与手动序号直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Number.ShouldBe(1);

        window.SelectNote(0);

        // 数字类型：阿拉伯 → 罗马
        window.SetNumberFormat(NumberFormat.Roman);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Format.ShouldBe(NumberFormat.Roman);

        // 手动序号：改成 7，显示也跟着变
        window.NumberBox.Text = "7";
        window.CommitNumberBox();
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Number.ShouldBe(7);
        window.NumberBox.Text.ShouldBe("7");

        // 标签：留空显示序号，填了就覆盖
        window.LabelBox.Text = "第一步";
        window.CommitLabelBox();
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Label.ShouldBe("第一步");

        window.Close();
    }

    [AvaloniaFact]
    public void 水印_属性栏文本直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.WatermarkToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseUp(new Point(200, 200), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<WatermarkAnnotation>().Text.ShouldBe("Sparrow");

        window.SelectNote(0);
        window.WatermarkBox.Text = "机密文件";
        window.CommitWatermarkBox();
        window.Notes[0].ShouldBeOfType<WatermarkAnnotation>().Text.ShouldBe("机密文件");

        window.Close();
    }

    [AvaloniaFact]
    public void 文字_字体对齐背景描边直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.TextToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(170, 170), MouseButton.Left);
        window.MouseUp(new Point(170, 170), MouseButton.Left);

        window.TextNoteBox.Text = "Hello";
        window.CommitTextNote();
        window.Notes.Count.ShouldBe(1);

        window.SelectNote(0);

        window.SetTextAlign(TextAlign.Center);
        window.Notes[0].ShouldBeOfType<TextAnnotation>().Align.ShouldBe(TextAlign.Center);

        window.SetTextBackground(0xFF000000);
        window.Notes[0].ShouldBeOfType<TextAnnotation>().BackgroundColor.ShouldBe(0xFF000000u);

        window.SetTextStrokeWidth(2);
        window.Notes[0].ShouldBeOfType<TextAnnotation>().StrokeWidth.ShouldBe(2);

        window.SetTextStrokeColor(0xFFFFFFFF);
        window.Notes[0].ShouldBeOfType<TextAnnotation>().StrokeColor.ShouldBe(0xFFFFFFFFu);

        window.ItalicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Notes[0].ShouldBeOfType<TextAnnotation>().Italic.ShouldBeTrue();

        window.Close();
    }
}
