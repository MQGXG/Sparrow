using Shouldly;
using Sparrow.Plugin.Ocr;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// OCR 整理模式合成（算法移植自 OmniSeeker 的 OcrTextSorter）。
/// 行框始终由默认聚类生成，这里的断言针对不同模式的全文输出。
/// </summary>
public sealed class OcrLayoutComposerTests
{
    private static OcrRegion Region(string text, int x, int y, int width, int height, float score = 0.9f) =>
        new(text, x, y, width, height, score);

    [Fact]
    public void 按行模式_同行的块用空格拼接()
    {
        var regions = new[]
        {
            Region("HELLO", 0, 0, 100, 40),
            Region("OCR", 120, 2, 60, 40)
        };

        OcrTextComposer.ComposeText(regions, OcrLayoutMode.Line, "fallback")
            .ShouldBe("HELLO OCR");
    }

    [Fact]
    public void 原文模式_直接返回worker全文()
    {
        var regions = new[]
        {
            Region("按行重排的结果", 0, 0, 100, 40)
        };

        OcrTextComposer.ComposeText(regions, OcrLayoutMode.Raw, "worker 原文")
            .ShouldBe("worker 原文");
    }

    [Fact]
    public void 表格模式_列间用Tab分隔()
    {
        var regions = new[]
        {
            Region("名称", 0, 0, 60, 30),
            Region("数量", 200, 0, 60, 30),
            Region("苹果", 0, 50, 60, 30),
            Region("12", 200, 50, 60, 30)
        };

        var text = OcrTextComposer.ComposeText(regions, OcrLayoutMode.Table, "");

        var rows = text.Split(Environment.NewLine);
        rows.Length.ShouldBe(2);
        rows[0].ShouldBe("名称\t数量");
        rows[1].ShouldBe("苹果\t12");
    }

    [Fact]
    public void 表格模式_阈值比按行更严格()
    {
        // 两块垂直相距 0.4 倍行高：按行（0.5）合并成一行，表格（0.25）保持两行
        var regions = new[]
        {
            Region("上", 0, 0, 60, 20),    // 中心 10
            Region("下", 0, 10, 60, 20)    // 中心 20，平均行高 20
        };

        var line = OcrTextComposer.ComposeText(regions, OcrLayoutMode.Line, "");
        var table = OcrTextComposer.ComposeText(regions, OcrLayoutMode.Table, "");

        line.Split(Environment.NewLine).Length.ShouldBe(1);
        table.Split(Environment.NewLine).Length.ShouldBe(2);
    }

    [Fact]
    public void 按位置模式_水平错开的块用空格还原距离()
    {
        var regions = new[]
        {
            Region("左", 0, 0, 60, 30),
            Region("右", 400, 0, 60, 30)
        };

        var text = OcrTextComposer.ComposeText(regions, OcrLayoutMode.Position, "");

        text.ShouldStartWith("左");
        text.ShouldEndWith("右");
        // 两块相距很远，中间应该是成片的空格而不是单个空格
        text.ShouldContain("    ");
        text.Count(c => c == ' ').ShouldBeGreaterThan(3);
    }

    [Fact]
    public void 低分块_所有模式下都被过滤()
    {
        var regions = new[]
        {
            Region("正文", 0, 0, 60, 30, 0.95f),
            Region("_", 0, 100, 20, 30, 0.36f)
        };

        foreach (var mode in new[] { OcrLayoutMode.Line, OcrLayoutMode.Raw, OcrLayoutMode.Position, OcrLayoutMode.Table })
        {
            if (mode == OcrLayoutMode.Raw)
                continue;

            OcrTextComposer.ComposeText(regions, mode, "").ShouldNotContain("_");
        }
    }

    [Fact]
    public void 没有可用块_返回空串()
    {
        OcrTextComposer.ComposeText([], OcrLayoutMode.Table, "fallback").ShouldBeEmpty();
        OcrTextComposer.ComposeText([], OcrLayoutMode.Position, "fallback").ShouldBeEmpty();
    }
}
