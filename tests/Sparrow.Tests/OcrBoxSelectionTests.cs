using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Shouldly;
using SkiaSharp;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>OCR 行框：多选、阅读顺序合并、三态外观。</summary>
public class OcrBoxSelectionTests
{
    [Fact]
    public void 合并_同一行按X排_行间换行()
    {
        var boxes = new[]
        {
            ("A2", new Rect(0, 90, 40, 20)),
            ("B2", new Rect(50, 90, 40, 20)),
            ("A1", new Rect(0, 50, 40, 20))
        };

        CaptureWindow.MergeOcrText(boxes).ShouldBe("A1\nA2 B2");
    }

    [Fact]
    public void 合并_中文之间不补空格_西文之间补()
    {
        CaptureWindow.MergeOcrText([
            ("订单", new Rect(0, 0, 40, 20)),
            ("编号", new Rect(40, 0, 40, 20))
        ]).ShouldBe("订单编号");

        CaptureWindow.MergeOcrText([
            ("Order", new Rect(0, 0, 40, 20)),
            ("ID", new Rect(40, 0, 20, 20))
        ]).ShouldBe("Order ID");
    }

    [Fact]
    public void 合并_忽略空文本和空集合()
    {
        CaptureWindow.MergeOcrText([]).ShouldBe(string.Empty);

        CaptureWindow.MergeOcrText([
            ("  ", new Rect(0, 0, 10, 10)),
            ("A", new Rect(0, 0, 10, 10))
        ]).ShouldBe("A");
    }

    [AvaloniaFact]
    public void 行框_默认红框_切换选中_再切换取消()
    {
        var window = CreateWithOcrLines();
        var box = (Rectangle)window.OcrLayer.Children[0];

        // 默认：红框 1px + 淡红底（12%）
        box.StrokeThickness.ShouldBe(1);
        box.Fill.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0x1F);

        window.ToggleOcrBoxSelection(box);
        box.StrokeThickness.ShouldBe(2);
        window.OcrStatus.Text.ShouldNotBeNull().ShouldContain("已选 1");

        window.ToggleOcrBoxSelection(box);
        box.StrokeThickness.ShouldBe(1);
        window.OcrStatus.Text.ShouldNotBeNull().ShouldNotContain("已选");

        window.Close();
    }

    [AvaloniaFact]
    public void 选中两行_CtrlC只复制这两行()
    {
        var window = CreateWithOcrLines();
        string? copied = null;
        window.CopyText = text => copied = text;

        window.ToggleOcrBoxSelection((Rectangle)window.OcrLayer.Children[0]);
        window.ToggleOcrBoxSelection((Rectangle)window.OcrLayer.Children[1]);

        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);

        copied.ShouldBe("第一行\n第二行");

        window.Close();
    }

    [Fact]
    public void 字符定位_点在字上是该字_行外钳到首尾()
    {
        var line = new OcrTextLine("机械键盘", 100, 50, 80, 20);
        var boxes = OcrTextLayout.Split(line);

        OcrTextLayout.IndexAt(boxes, new Point(105, 60)).ShouldBe(0);
        OcrTextLayout.IndexAt(boxes, new Point(125, 60)).ShouldBe(1);
        // 行左外侧 / 右外侧钳到 0 / Count，方便拖到行首行尾
        OcrTextLayout.IndexAt(boxes, new Point(50, 60)).ShouldBe(0);
        OcrTextLayout.IndexAt(boxes, new Point(400, 60)).ShouldBe(4);
    }

    [Fact]
    public void 字符区间_取子串和包围盒()
    {
        var line = new OcrTextLine("机械键盘", 100, 50, 80, 20);
        var boxes = OcrTextLayout.Split(line);

        OcrTextLayout.TextForRange(line.Text, 1, 2).ShouldBe("械键");

        var box = OcrTextLayout.BoxesForRange(boxes, 1, 2).Single();
        box.X.ShouldBe(120, 0.01);
        box.Width.ShouldBe(40, 0.01);
        box.Height.ShouldBe(20, 0.01);
    }

    [AvaloniaFact]
    public void 标记_同一区间再点一次取消()
    {
        var window = CreateWithOcrLines();
        window.SetOcrSelection(0, 0, 3);

        window.ApplyOcrMark(TextMarkKind.Wave);
        window.OcrMarkCount.ShouldBe(1);

        window.ApplyOcrMark(TextMarkKind.Wave);
        window.OcrMarkCount.ShouldBe(0);

        window.Close();
    }

    [Fact]
    public void 导出_波浪线烧进图片()
    {
        var png = CreateWhitePng(100, 100);
        var mark = new OcrTextMark(
            TextMarkKind.Wave,
            [new Rect(10, 20, 60, 20)],
            OcrTextMarks.ColorFor(TextMarkKind.Wave),
            0);

        var composed = AnnotationRenderer.Compose(png, [], 1, [mark]);

        using var decoded = SkiaSharp.SKBitmap.Decode(composed);
        // 波浪线在框底，沿 x 扫描必然碰到非白像素
        var touched = false;
        for (var x = 12; x < 68; x++)
        {
            var pixel = decoded.GetPixel(x, 39);
            if (pixel.Red > 150 && pixel.Green < 150)
            {
                touched = true;
                break;
            }
        }

        touched.ShouldBeTrue();
        // 框外仍是白的
        ((int)decoded.GetPixel(90, 90).Red).ShouldBeGreaterThan(200);
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

    private static CaptureWindow CreateWithOcrLines()
    {
        var window = new CaptureWindow();
        window.Show();
        window.Width = 800;
        window.Height = 600;

        // 先框一个选区，OCR 层才有地方放
        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(500, 400));
        window.MouseUp(new Point(500, 400), MouseButton.Left);

        window.ShowOcrResult(new CaptureOcrResult(
            "第一行\n第二行",
            [
                new OcrTextLine("第一行", 120, 150, 120, 24),
                new OcrTextLine("第二行", 120, 190, 120, 24)
            ]));

        return window;
    }
}
