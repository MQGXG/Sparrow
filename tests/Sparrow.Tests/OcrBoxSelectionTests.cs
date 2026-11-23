using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Shouldly;
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
