using Shouldly;
using Sparrow.Plugin.Ocr;
using Xunit;

namespace Sparrow.Tests;

public sealed class OcrTextComposerTests
{
    [Fact]
    public void 同一行的多个块_用空格连接()
    {
        var regions = new[]
        {
            new OcrRegion("HELLO", 0, 0, 50, 20, 0.99f),
            new OcrRegion("OCR", 60, 0, 30, 20, 0.98f)
        };

        OcrTextComposer.Compose(regions).ShouldBe("HELLO OCR");
    }

    [Fact]
    public void 不同行_用换行连接()
    {
        var regions = new[]
        {
            new OcrRegion("第一行", 0, 0, 60, 20, 0.99f),
            new OcrRegion("第二行", 0, 40, 60, 20, 0.99f)
        };

        OcrTextComposer.Compose(regions)
            .ShouldBe($"第一行{Environment.NewLine}第二行");
    }

    [Fact]
    public void 乱序输入_按坐标重排()
    {
        var regions = new[]
        {
            new OcrRegion("B", 80, 0, 20, 20, 0.9f),
            new OcrRegion("A", 0, 0, 60, 20, 0.9f)
        };

        OcrTextComposer.Compose(regions).ShouldBe("A B");
    }

    [Fact]
    public void 行内空白_会被裁剪()
    {
        var regions = new[]
        {
            new OcrRegion("  HELLO  ", 0, 0, 50, 20, 0.9f),
            new OcrRegion("OCR", 60, 0, 30, 20, 0.9f)
        };

        OcrTextComposer.Compose(regions).ShouldBe("HELLO OCR");
    }

    [Fact]
    public void 空列表_返回空字符串()
    {
        OcrTextComposer.Compose([]).ShouldBe(string.Empty);
    }

    [Fact]
    public void 低置信度块_被过滤()
    {
        var regions = new[]
        {
            new OcrRegion("Привет", 0, 0, 100, 30, 0.93f),
            new OcrRegion("_", 120, 10, 5, 14, 0.36f)
        };

        OcrTextComposer.Compose(regions).ShouldBe("Привет");
    }

    [Fact]
    public void 空白文本块_被过滤()
    {
        var regions = new[]
        {
            new OcrRegion("HELLO", 0, 0, 60, 20, 0.9f),
            new OcrRegion("   ", 70, 0, 20, 20, 0.9f)
        };

        OcrTextComposer.Compose(regions).ShouldBe("HELLO");
    }

    [Fact]
    public void 行框_覆盖该行所有文本块()
    {
        var regions = new[]
        {
            new OcrRegion("HELLO", 10, 20, 50, 20, 0.99f),
            new OcrRegion("OCR", 70, 22, 30, 18, 0.98f),
            new OcrRegion("第二行", 0, 60, 60, 20, 0.97f)
        };

        var lines = OcrTextComposer.ComposeLines(regions);

        lines.Count.ShouldBe(2);
        lines[0].Text.ShouldBe("HELLO OCR");
        lines[0].X.ShouldBe(10);
        lines[0].Y.ShouldBe(20);
        lines[0].Width.ShouldBe(90);
        lines[0].Height.ShouldBe(20);
        lines[1].Text.ShouldBe("第二行");

        string.Join(Environment.NewLine, lines.Select(line => line.Text))
            .ShouldBe(OcrTextComposer.Compose(regions));
    }

    [Fact]
    public void 空列表_返回空行框()
    {
        OcrTextComposer.ComposeLines([]).ShouldBeEmpty();
    }

    // === 表格：格子级行 ===

    /// <summary>3 行 × 3 列的表格，单元格间距足够大，不会被聚成同一行。</summary>
    private static OcrRegion[] Table() =>
    [
        new("订单号", 0, 0, 80, 20, 0.98f),
        new("客户", 140, 0, 40, 20, 0.98f),
        new("金额", 260, 0, 40, 20, 0.98f),

        new("ORD-001", 0, 40, 80, 20, 0.98f),
        new("张三", 140, 40, 40, 20, 0.98f),
        new("399.00", 260, 40, 40, 20, 0.98f),

        new("ORD-002", 0, 80, 80, 20, 0.98f),
        new("李四", 140, 80, 40, 20, 0.98f),
        new("128.00", 260, 80, 40, 20, 0.98f)
    ];

    [Fact]
    public void 三行三列_判定为表格()
    {
        OcrTextComposer.LooksLikeTable(Table()).ShouldBeTrue();
    }

    [Fact]
    public void 纯文本段落_不判定为表格()
    {
        var regions = new[]
        {
            new OcrRegion("第一行很长的一段正文", 0, 0, 300, 20, 0.98f),
            new OcrRegion("第二行也是正文", 0, 40, 200, 20, 0.98f),
            new OcrRegion("第三行", 0, 80, 100, 20, 0.98f)
        };

        OcrTextComposer.LooksLikeTable(regions).ShouldBeFalse();
    }

    [Fact]
    public void 列位置对不齐_不判定为表格()
    {
        var regions = new[]
        {
            new OcrRegion("A", 0, 0, 20, 20, 0.98f),
            new OcrRegion("B", 200, 0, 20, 20, 0.98f),
            new OcrRegion("C", 400, 0, 20, 20, 0.98f),
            new OcrRegion("D", 0, 40, 20, 20, 0.98f),
            new OcrRegion("E", 600, 40, 20, 20, 0.98f),
            new OcrRegion("F", 800, 40, 20, 20, 0.98f)
        };

        OcrTextComposer.LooksLikeTable(regions).ShouldBeFalse();
    }

    [Fact]
    public void 表格区域级行_一格一行且按行列顺序()
    {
        var lines = OcrTextComposer.ComposeRegionLines(Table());

        lines.Count.ShouldBe(9);
        lines.Select(line => line.Text).ShouldBe(
        [
            "订单号", "客户", "金额",
            "ORD-001", "张三", "399.00",
            "ORD-002", "李四", "128.00"
        ]);

        // 每行框就是原始单元格，不再向外扩成整行
        lines[0].Width.ShouldBe(80);
        lines[3].X.ShouldBe(0);
        lines[4].X.ShouldBe(140);
    }

    [Fact]
    public void 表格区域级行_文本仍按整行合成()
    {
        OcrTextComposer.Compose(Table())
            .Split(Environment.NewLine)
            .ShouldBe(["订单号 客户 金额", "ORD-001 张三 399.00", "ORD-002 李四 128.00"]);
    }
}
