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
public sealed partial class AnnotationTests
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

}
