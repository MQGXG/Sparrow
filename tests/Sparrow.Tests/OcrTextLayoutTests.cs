using Avalonia;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk.Payloads;
using Shouldly;
using Xunit;

namespace Sparrow.Tests;

/// <summary>OCR 字符级近似排版：Paddle 只给行框，这里负责拆成字框。</summary>
public class OcrTextLayoutTests
{
    [Fact]
    public void 纯中文_按等宽拆分且铺满整行()
    {
        var line = new OcrTextLine("机械键盘", 100, 50, 80, 20);

        var boxes = OcrTextLayout.Split(line);

        boxes.Count.ShouldBe(4);
        boxes[0].Text.ShouldBe("机");
        boxes[3].Text.ShouldBe("盘");

        // 每个字 20 宽
        boxes[0].Box.Width.ShouldBe(20, 0.01);
        // 首尾对齐行框
        boxes[0].Box.X.ShouldBe(100, 0.01);
        boxes[^1].Box.Right.ShouldBe(180, 0.01);
    }

    [Fact]
    public void 纯西文_比中文窄()
    {
        var cjk = OcrTextLayout.Split(new OcrTextLine("机械键盘", 0, 0, 80, 20));
        var latin = OcrTextLayout.Split(new OcrTextLine("Machine", 0, 0, 80, 20));

        // 同样的行宽，西文单字更窄
        latin[0].Box.Width.ShouldBeLessThan(cjk[0].Box.Width);
        // 但总宽一致
        latin[^1].Box.Right.ShouldBe(80, 0.01);
    }

    [Fact]
    public void 中英混排_中文比英文宽()
    {
        var line = new OcrTextLine("键盘K8", 0, 0, 100, 20);

        var boxes = OcrTextLayout.Split(line);

        boxes.Count.ShouldBe(4);
        boxes[0].Box.Width.ShouldBeGreaterThan(boxes[2].Box.Width);   // 键 > K
        boxes[^1].Box.Right.ShouldBe(100, 0.01);
    }

    [Fact]
    public void 空文本或非法框_返回空()
    {
        OcrTextLayout.Split(new OcrTextLine("", 0, 0, 10, 10)).Count.ShouldBe(0);
        OcrTextLayout.Split(new OcrTextLine("abc", 0, 0, 0, 10)).Count.ShouldBe(0);
        OcrTextLayout.Split(new OcrTextLine("abc", 0, 0, 10, 0)).Count.ShouldBe(0);
    }

    [Fact]
    public void 代理对算一个字符()
    {
        var line = new OcrTextLine("A😀B", 0, 0, 30, 20);

        var boxes = OcrTextLayout.Split(line);

        boxes.Count.ShouldBe(3);
        boxes[1].Text.ShouldBe("😀");
    }

    [Fact]
    public void 命中测试_落在字内返回下标_落在行外返回负一()
    {
        var line = new OcrTextLine("机械键盘", 100, 50, 80, 20);
        var boxes = OcrTextLayout.Split(line);

        OcrTextLayout.HitIndex(boxes, new Point(105, 60)).ShouldBe(0);
        OcrTextLayout.HitIndex(boxes, new Point(175, 60)).ShouldBe(3);
        OcrTextLayout.HitIndex(boxes, new Point(90, 60)).ShouldBe(-1);
    }
}
