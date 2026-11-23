using Shouldly;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 识别文本的「格式 / 标点」整理：纯文本变换，不改行框。
/// </summary>
public sealed class OcrTextTransformTests
{
    [Fact]
    public void 去掉换行_中文直接相连_英文补空格()
    {
        OcrTextTransform.RemoveLineBreaks("第一行\n第二行").ShouldBe("第一行第二行");
        OcrTextTransform.RemoveLineBreaks("Hello\nworld").ShouldBe("Hello world");
    }

    [Fact]
    public void 智能排版_把硬折行的句子接回去()
    {
        OcrTextTransform.SmartTypeset("这是一个被硬折\n行的句子。")
            .ShouldBe("这是一个被硬折行的句子。");
    }

    [Fact]
    public void 智能排版_保留句末换行()
    {
        OcrTextTransform.SmartTypeset("第一句。\n第二句。")
            .ShouldBe("第一句。\n第二句。");
    }

    [Fact]
    public void 智能排版_表格行不会被合并()
    {
        OcrTextTransform.SmartTypeset("名称\t数量\n苹果\t12")
            .ShouldBe("名称\t数量\n苹果\t12");
    }

    [Fact]
    public void 标点半角与全角互转()
    {
        OcrTextTransform.ToHalfWidth("你好，世界（测试）。").ShouldBe("你好,世界(测试).");
        OcrTextTransform.ToFullWidth("你好,世界(测试).").ShouldBe("你好，世界（测试）。");
    }

    [Fact]
    public void 不处理时原样返回()
    {
        const string text = "第一行\n第二行，标点不变";

        OcrTextTransform.Apply(text, OcrTextTransform.FormatNone, OcrTextTransform.PunctuationNone)
            .ShouldBe(text);

        // 未知取值也按不处理
        OcrTextTransform.Apply(text, "???", "???")
            .ShouldBe(text);
    }

    [Fact]
    public void 表格区域_走格子级行框且套用文本整理()
    {
        // 3 行 × 3 列：每格一个 region，LooksLikeTable 判定为表格
        var json = """
        {
          "text": "订单号 客户 金额 ORD-001 张三 399.00 ORD-002 李四 128.00",
          "regions": [
            {"text":"订单号","x":0,"y":0,"width":80,"height":20,"score":0.98},
            {"text":"客户","x":140,"y":0,"width":40,"height":20,"score":0.98},
            {"text":"金额","x":260,"y":0,"width":40,"height":20,"score":0.98},
            {"text":"ORD-001","x":0,"y":40,"width":80,"height":20,"score":0.98},
            {"text":"张三","x":140,"y":40,"width":40,"height":20,"score":0.98},
            {"text":"399.00","x":260,"y":40,"width":40,"height":20,"score":0.98},
            {"text":"ORD-002","x":0,"y":80,"width":80,"height":20,"score":0.98},
            {"text":"李四","x":140,"y":80,"width":40,"height":20,"score":0.98},
            {"text":"128.00","x":260,"y":80,"width":40,"height":20,"score":0.98}
          ]
        }
        """;

        var payload = OcrOutputComposer.Compose(
            json,
            OcrLayoutMode.Line,
            OcrTextTransform.FormatRemove,
            OcrTextTransform.PunctuationHalf);

        // 行框是格子级（9 个），不是合并后的 3 行
        payload.Lines.Count.ShouldBe(9);
        payload.Lines[0].Text.ShouldBe("订单号");
        payload.Lines[4].Text.ShouldBe("张三");

        // 复制文本仍是整行合并，并套用「去掉换行」：
        // 表格行里的空格是行内块之间的分隔，不该被当成换行去掉
        payload.Text.ShouldContain("订单号 客户 金额");
    }
}
