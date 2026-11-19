using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 复制变换是纯函数，规则对齐 Witch Clipboard：
/// 大小写只作用于字母，中文原样保留。
/// </summary>
public sealed class PasteTransformsTests
{
    [Theory]
    [InlineData("hello world again", PasteTransform.Upper, "HELLO WORLD AGAIN")]
    [InlineData("HELLO World 中文", PasteTransform.Lower, "hello world 中文")]
    [InlineData("hello   world", PasteTransform.Capitalize, "Hello World")]
    [InlineData("hello. world! again?", PasteTransform.Sentence, "Hello. World! Again?")]
    [InlineData("hello world again", PasteTransform.Camel, "helloWorldAgain")]
    [InlineData("user_name-id", PasteTransform.Camel, "userNameId")]
    [InlineData("  留白  ", PasteTransform.Trim, "留白")]
    [InlineData("原样 <b>保留</b>", PasteTransform.PlainText, "原样 <b>保留</b>")]
    public void 变换结果符合预期(string input, PasteTransform transform, string expected)
    {
        PasteTransforms.Apply(input, transform).ShouldBe(expected);
    }

    [Fact]
    public void 中文句子不会被大小写变换破坏()
    {
        PasteTransforms.Apply("你好 世界", PasteTransform.Upper).ShouldBe("你好 世界");
        PasteTransforms.Apply("你好 世界", PasteTransform.Camel).ShouldBe("你好世界");
    }

    [Fact]
    public void 每种变换都有中文标签()
    {
        foreach (var transform in Enum.GetValues<PasteTransform>())
            PasteTransforms.Label(transform).ShouldNotBeNullOrWhiteSpace();
    }
}
