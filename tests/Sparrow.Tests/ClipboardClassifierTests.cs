using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 自动标签的判定边界：宁可不标，也不要把普通句子误标成链接/JSON。
/// </summary>
public sealed class ClipboardClassifierTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/a?b=1")]
    [InlineData("www.example.com")]
    public void 整个内容是一个链接_标为链接(string text)
    {
        ClipboardClassifier.ClassifyText(text).ShouldContain(ClipboardClassifier.TagLink);
    }

    [Fact]
    public void 句子里夹着链接_不标为链接()
    {
        ClipboardClassifier.ClassifyText("看看这个 https://example.com 写得不错")
            .ShouldNotContain(ClipboardClassifier.TagLink);
    }

    [Theory]
    [InlineData("#fff")]
    [InlineData("#E5A44C")]
    [InlineData("rgba(229, 164, 76, 0.8)")]
    public void 颜色值_标为颜色(string text)
    {
        ClipboardClassifier.ClassifyText(text).ShouldContain(ClipboardClassifier.TagColor);
    }

    [Theory]
    [InlineData(@"{""a"":1}")]
    [InlineData("[1, 2, 3]")]
    public void 合法JSON_标为JSON(string text)
    {
        ClipboardClassifier.ClassifyText(text).ShouldContain(ClipboardClassifier.TagJson);
    }

    [Fact]
    public void 坏JSON_不标()
    {
        ClipboardClassifier.ClassifyText("{a: }").ShouldNotContain(ClipboardClassifier.TagJson);
    }

    [Fact]
    public void Windows路径_标为路径()
    {
        ClipboardClassifier.ClassifyText(@"D:\mycodeHub\Sparrow\README.md")
            .ShouldContain(ClipboardClassifier.TagPath);
    }

    [Fact]
    public void 文件条目_全是图片才算图片()
    {
        ClipboardClassifier.ClassifyFiles([@"C:\a.png", @"C:\b.jpg"])
            .ShouldBe([ClipboardClassifier.TagImage]);

        ClipboardClassifier.ClassifyFiles([@"C:\a.png", @"C:\b.pdf"])
            .ShouldBe([ClipboardClassifier.TagFile]);
    }

    [Fact]
    public void 空白文本_没有标签()
    {
        ClipboardClassifier.ClassifyText("   ").ShouldBeEmpty();
    }
}
