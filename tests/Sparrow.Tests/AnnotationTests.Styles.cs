// AnnotationTests 分部类：形状、透明度、手绘、属性栏与调色板。
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
}
