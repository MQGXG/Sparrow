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
}
