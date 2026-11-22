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

/// <summary>对话气泡：尾巴几何、导出接缝、换行、文字编辑与快捷键。</summary>
public sealed class BubbleTests
{
    [Fact]
    public void 气泡_导出边框和尾巴且内部保持透明()
    {
        var png = CreateWhitePng(200, 200);
        var bubble = new SpeechBubbleAnnotation(
            new Rect(30, 30, 80, 50), new Point(140, 120), string.Empty,
            0xFF000000, 2, 16, ShapeFill.None);

        var composed = AnnotationRenderer.Compose(png, [bubble], 1);
        using var decoded = SKBitmap.Decode(composed);

        decoded.GetPixel(70, 30).Red.ShouldBeLessThan((byte)120);   // 上边框
        decoded.GetPixel(30, 55).Red.ShouldBeLessThan((byte)120);   // 左边框
        decoded.GetPixel(70, 55).Red.ShouldBe((byte)255);           // 无填充

        // 尾巴只描两条边，取第一条边的中点
        var (first, _) = AnnotationGeometry.BubbleTailBase(bubble.Rect, bubble.Tail);
        var mid = new Point((first.X + bubble.Tail.X) / 2, (first.Y + bubble.Tail.Y) / 2);
        DarkestRed(decoded, (int)mid.X - 2, (int)mid.Y - 2, (int)mid.X + 3, (int)mid.Y + 3)
            .ShouldBeLessThan((byte)120);
    }

    [Fact]
    public void 气泡_淡色填充覆盖内部()
    {
        var png = CreateWhitePng(160, 160);
        var bubble = new SpeechBubbleAnnotation(
            new Rect(30, 30, 100, 60), new Point(140, 140), string.Empty,
            0xFFFF0000, 2, 16, ShapeFill.Soft);

        using var decoded = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [bubble], 1));

        decoded.GetPixel(80, 60).Green.ShouldBeLessThan((byte)250);
    }

    [Fact]
    public void 气泡文字_导出在内部留下笔画()
    {
        var png = CreateWhitePng(200, 200);
        var bubble = new SpeechBubbleAnnotation(
            new Rect(30, 30, 120, 80), new Point(170, 150), "Hi",
            0xFF000000, 2, 16, ShapeFill.None);

        using var decoded = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [bubble], 1));

        DarkestRed(decoded, 50, 50, 130, 95).ShouldBeLessThan((byte)160);
    }

    [Fact]
    public void 气泡尾尖_落在角上时推出矩形()
    {
        var rect = new Rect(0, 0, 100, 60);

        var pushed = AnnotationGeometry.BubbleTail(rect, new Point(0, 0));
        rect.Contains(pushed).ShouldBeFalse();
        Math.Sqrt(pushed.X * pushed.X + pushed.Y * pushed.Y)
            .ShouldBe(16, 0.01);

        // 已经在外面：保持原样
        var outside = new Point(150, 30);
        AnnotationGeometry.BubbleTail(rect, outside).ShouldBe(outside);
    }

    [Fact]
    public void 气泡尾巴_接在最近的一条边上()
    {
        var rect = new Rect(0, 0, 100, 60);

        var (rightA, rightB) = AnnotationGeometry.BubbleTailBase(rect, new Point(150, 30));
        rightA.X.ShouldBe(100, 0.01);
        rightB.X.ShouldBe(100, 0.01);
        rightA.Y.ShouldBeLessThan(rightB.Y);

        var (bottomA, bottomB) = AnnotationGeometry.BubbleTailBase(rect, new Point(50, 100));
        bottomA.Y.ShouldBe(60, 0.01);
        bottomB.Y.ShouldBe(60, 0.01);
        bottomA.X.ShouldBeLessThan(bottomB.X);
    }

    [Fact]
    public void 气泡文字_按宽度换行()
    {
        var lines = AnnotationText.Wrap("Hello world this is a long sentence", 14, 60);

        lines.Count.ShouldBeGreaterThan(1);
        foreach (var line in lines)
            AnnotationText.Measure(line, 14).ShouldBeLessThanOrEqualTo(61);
    }

    [AvaloniaFact]
    public void 截图窗口_气泡拖拽生成并可双击改文字()
    {
        var window = CreateWindowWithSelection();

        window.KeyPress(Key.Q, RawInputModifiers.None, PhysicalKey.Q, null);
        window.BubbleToolButton.Classes.Contains("selected").ShouldBeTrue();
        window.WidthGroup.IsVisible.ShouldBeTrue();
        window.FillGroup.IsVisible.ShouldBeTrue();
        window.FontGroup.IsVisible.ShouldBeTrue();
        window.DashGroup.IsVisible.ShouldBeFalse();

        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(300, 260));
        window.MouseUp(new Point(300, 260), MouseButton.Left);

        var bubble = window.Notes[0].ShouldBeOfType<SpeechBubbleAnnotation>();
        bubble.Text.ShouldBeEmpty();
        bubble.Rect.Width.ShouldBe(150, 0.01);
        bubble.Rect.Height.ShouldBe(110, 0.01);
        bubble.Rect.Contains(bubble.Tail).ShouldBeFalse("尾尖要被推到矩形外");

        window.EditNoteText(0).ShouldBeTrue();
        window.TextNoteBox.Text.ShouldBeEmpty();
        window.TextNoteBox.Text = "需要修改";
        window.CommitTextNote();

        window.Notes[0].ShouldBeOfType<SpeechBubbleAnnotation>().Text.ShouldBe("需要修改");

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_气泡工具点已有气泡不会叠一层()
    {
        var window = CreateWindowWithSelection();
        window.BubbleToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(300, 260));
        window.MouseUp(new Point(300, 260), MouseButton.Left);
        window.Notes.Count.ShouldBe(1);

        // 点在气泡内部：选中而不是新建
        window.MouseDown(new Point(220, 200), MouseButton.Left);
        window.MouseUp(new Point(220, 200), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.NoteLayer.Children.Count.ShouldBeGreaterThan(1);   // 气泡 + 选中虚线框

        window.Close();
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

    private static byte DarkestRed(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        byte darkest = 255;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            var red = bitmap.GetPixel(x, y).Red;
            if (red < darkest)
                darkest = red;
        }

        return darkest;
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
}
