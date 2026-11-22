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

/// <summary>水印：平铺落点、导出像素、单击铺满选区、双击改写文字。</summary>
public sealed class WatermarkTests
{
    [Fact]
    public void 水印_导出只在区域内留下文字()
    {
        var png = CreateWhitePng(200, 200);
        var watermark = new WatermarkAnnotation(
            new Rect(40, 40, 120, 120), "WM", 0xFF000000, 24,
            Opacity: 1, Angle: 0, Spacing: 60);

        var composed = AnnotationRenderer.Compose(png, [watermark], 1);
        using var decoded = SKBitmap.Decode(composed);

        HasInk(decoded, 40, 40, 160, 160).ShouldBeTrue("区域内应该出现水印文字");
        HasInk(decoded, 0, 0, 35, 35).ShouldBeFalse("区域外必须保持原图");
    }

    [Fact]
    public void 水印_旋转后仍然只在区域内留下文字()
    {
        var png = CreateWhitePng(200, 200);
        var watermark = new WatermarkAnnotation(
            new Rect(40, 40, 120, 120), "WM", 0xFF000000, 24,
            Opacity: 1, Angle: 45, Spacing: 60);

        var composed = AnnotationRenderer.Compose(png, [watermark], 1);
        using var decoded = SKBitmap.Decode(composed);

        HasInk(decoded, 40, 40, 160, 160).ShouldBeTrue("旋转后区域内仍有文字");
        HasInk(decoded, 0, 0, 35, 35).ShouldBeFalse("裁剪框不能跟着旋转漏到外面");
    }

    [Fact]
    public void 水印_透明度越低颜色越浅()
    {
        var png = CreateWhitePng(200, 200);
        var solid = new WatermarkAnnotation(
            new Rect(40, 40, 120, 120), "WM", 0xFF000000, 24,
            Opacity: 1, Angle: 0, Spacing: 60);
        var faint = solid with { Opacity = 0.2 };

        using var strong = SKBitmap.Decode(AnnotationRenderer.Compose(png, [solid], 1));
        using var light = SKBitmap.Decode(AnnotationRenderer.Compose(png, [faint], 1));

        DarkestRed(strong, 40, 40, 160, 160)
            .ShouldBeLessThan(DarkestRed(light, 40, 40, 160, 160));
    }

    [Fact]
    public void 水印平铺_超限时放大间距而不是跳过()
    {
        var area = new Rect(0, 0, 1000, 1000);

        WatermarkLayout.Count(area, 24)
            .ShouldBeGreaterThan(WatermarkLayout.TileCap, "24 间距下会超过上限");

        var fitted = WatermarkLayout.FitSpacing(area, 24);
        fitted.ShouldBeGreaterThan(24);
        WatermarkLayout.Count(area, fitted)
            .ShouldBeLessThanOrEqualTo(WatermarkLayout.TileCap);
        WatermarkLayout.Tiles(area, fitted, WatermarkLayout.TileCap)
            .Count().ShouldBeLessThanOrEqualTo(WatermarkLayout.TileCap);

        // 预览上限只限制画多少，不改变落点
        WatermarkLayout.Tiles(area, fitted, 10).Count().ShouldBe(10);
    }

    [Fact]
    public void 角度草稿_第二次拖拽才成立且拒绝退化边()
    {
        var draft = new AngleDraftState();
        draft.IsActive.ShouldBeFalse();

        draft.Begin(new Point(0, 0), new Point(100, 0));
        draft.IsActive.ShouldBeTrue();

        draft.TryBuild(new Point(0, 100), out var angle).ShouldBeTrue();
        angle.Vertex.ShouldBe(new Point(0, 0));
        angle.A.ShouldBe(new Point(100, 0));
        angle.B.ShouldBe(new Point(0, 100));

        // 零长度的第二条边：拒绝，但草稿保留，用户可以重试
        draft.TryBuild(new Point(0, 0), out _).ShouldBeFalse();
        draft.IsActive.ShouldBeTrue();

        draft.Clear();
        draft.IsActive.ShouldBeFalse();
        draft.TryBuild(new Point(0, 100), out _).ShouldBeFalse();
    }

    [AvaloniaFact]
    public void 截图窗口_水印单击铺满整个选区()
    {
        var window = CreateWindowWithSelection();

        window.KeyPress(Key.W, RawInputModifiers.None, PhysicalKey.W, null);
        window.WatermarkToolButton.Classes.Contains("selected").ShouldBeTrue();
        window.StyleRow.IsVisible.ShouldBeTrue();
        window.FontGroup.IsVisible.ShouldBeTrue();

        // 单击（不拖）= 整个选区
        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseUp(new Point(200, 200), MouseButton.Left);

        var watermark = window.Notes[0].ShouldBeOfType<WatermarkAnnotation>();
        watermark.Area.X.ShouldBe(0, 0.01);
        watermark.Area.Y.ShouldBe(0, 0.01);
        watermark.Area.Width.ShouldBe(300, 0.01);
        watermark.Area.Height.ShouldBe(200, 0.01);
        watermark.Text.ShouldBe("Sparrow");

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_水印拖拽限定平铺区域()
    {
        var window = CreateWindowWithSelection();
        window.WatermarkToolButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(150, 150), MouseButton.Left);
        window.MouseMove(new Point(250, 220));
        window.MouseUp(new Point(250, 220), MouseButton.Left);

        var watermark = window.Notes[0].ShouldBeOfType<WatermarkAnnotation>();
        watermark.Area.X.ShouldBe(50, 0.01);
        watermark.Area.Y.ShouldBe(50, 0.01);
        watermark.Area.Width.ShouldBe(100, 0.01);
        watermark.Area.Height.ShouldBe(70, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_水印双击改写文字()
    {
        var window = CreateWindowWithSelection();
        window.WatermarkToolButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseUp(new Point(200, 200), MouseButton.Left);

        window.EditNoteText(0).ShouldBeTrue();
        window.TextNoteBox.IsVisible.ShouldBeTrue();
        window.TextNoteBox.Text.ShouldBe("Sparrow");

        window.TextNoteBox.Text = "CONFIDENTIAL";
        window.CommitTextNote();

        window.Notes[0].ShouldBeOfType<WatermarkAnnotation>().Text.ShouldBe("CONFIDENTIAL");
        window.TextNoteBox.IsVisible.ShouldBeFalse();

        window.Close();
    }

    [AvaloniaFact]
    public void 橡皮擦_水印整个区域都可擦除()
    {
        var window = CreateWindowWithSelection();
        window.KeyPress(Key.W, RawInputModifiers.None, PhysicalKey.W, null);

        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseUp(new Point(200, 200), MouseButton.Left);
        window.Notes.Count.ShouldBe(1);

        var watermark = window.Notes[0].ShouldBeOfType<WatermarkAnnotation>();
        var click = GlyphPoint(watermark);

        window.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, null);
        window.MouseDown(new Point(click.X + 100, click.Y + 100), MouseButton.Left);
        window.MouseUp(new Point(click.X + 100, click.Y + 100), MouseButton.Left);

        window.Notes.ShouldBeEmpty();

        window.Close();
    }

    [AvaloniaFact]
    public void 截图窗口_水印工具点已有水印不会叠一层()
    {
        var window = CreateWindowWithSelection();
        window.WatermarkToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseUp(new Point(200, 200), MouseButton.Left);
        window.Notes.Count.ShouldBe(1);

        // 点在已有水印的文字上：只选中，不新建
        var watermark = window.Notes[0].ShouldBeOfType<WatermarkAnnotation>();
        var click = GlyphPoint(watermark);
        window.MouseDown(new Point(click.X + 100, click.Y + 100), MouseButton.Left);
        window.MouseUp(new Point(click.X + 100, click.Y + 100), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.NoteLayer.Children.Count.ShouldBe(2);   // 水印 + 选中虚线框

        window.Close();
    }

    /// <summary>取水印最靠近中心的一条文字，按旋转角换算回窗口坐标。</summary>
    private static Point GlyphPoint(WatermarkAnnotation watermark)
    {
        var spacing = WatermarkLayout.FitSpacing(watermark.Area, watermark.Spacing);
        var center = watermark.Area.Center;
        var tile = WatermarkLayout.Tiles(watermark.Area, spacing, WatermarkLayout.TileCap)
            .OrderBy(point => (point.X - center.X) * (point.X - center.X) +
                              (point.Y - center.Y) * (point.Y - center.Y))
            .First();

        var radians = watermark.Angle * Math.PI / 180;
        var dx = tile.X - center.X;
        var dy = tile.Y - center.Y;
        return new Point(
            center.X + dx * Math.Cos(radians) - dy * Math.Sin(radians),
            center.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
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
            if (bitmap.GetPixel(x, y).Red < 250)
                return true;
        }

        return false;
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
