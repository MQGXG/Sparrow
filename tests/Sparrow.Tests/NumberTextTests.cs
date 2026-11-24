using Shouldly;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>序号文本：五种数字类型 + 手动标签优先。</summary>
public sealed class NumberTextTests
{
    [Fact]
    public void 阿拉伯数字_直接返回数值()
    {
        NumberText.Format(0, NumberFormat.Arabic).ShouldBe("0");
        NumberText.Format(7, NumberFormat.Arabic).ShouldBe("7");
    }

    [Fact]
    public void 罗马数字_按符号贪心拼接()
    {
        NumberText.Format(1, NumberFormat.Roman).ShouldBe("I");
        NumberText.Format(4, NumberFormat.Roman).ShouldBe("IV");
        NumberText.Format(9, NumberFormat.Roman).ShouldBe("IX");
        NumberText.Format(40, NumberFormat.Roman).ShouldBe("XL");
        NumberText.Format(2026, NumberFormat.Roman).ShouldBe("MMXXVI");
    }

    [Fact]
    public void 罗马数字_超出3999回退阿拉伯()
    {
        NumberText.Format(4000, NumberFormat.Roman).ShouldBe("4000");
    }

    [Fact]
    public void 字母_双射二十六进制()
    {
        NumberText.Format(1, NumberFormat.LowerLetter).ShouldBe("a");
        NumberText.Format(26, NumberFormat.LowerLetter).ShouldBe("z");
        NumberText.Format(27, NumberFormat.LowerLetter).ShouldBe("aa");

        NumberText.Format(1, NumberFormat.UpperLetter).ShouldBe("A");
        NumberText.Format(28, NumberFormat.UpperLetter).ShouldBe("AB");
    }

    [Fact]
    public void 中文数字_按位权拼接并补零()
    {
        NumberText.Format(0, NumberFormat.Chinese).ShouldBe("零");
        NumberText.Format(10, NumberFormat.Chinese).ShouldBe("十");
        NumberText.Format(12, NumberFormat.Chinese).ShouldBe("十二");
        NumberText.Format(20, NumberFormat.Chinese).ShouldBe("二十");
        NumberText.Format(105, NumberFormat.Chinese).ShouldBe("一百零五");
        NumberText.Format(1005, NumberFormat.Chinese).ShouldBe("一千零五");
    }

    [Fact]
    public void 手动标签_覆盖自动序号()
    {
        NumberText.Format(3, NumberFormat.Roman, "第一步").ShouldBe("第一步");
        NumberText.Format(3, NumberFormat.Arabic, "  注意  ").ShouldBe("注意");
        NumberText.Format(3, NumberFormat.Arabic, "   ").ShouldBe("3");
        NumberText.Format(3, NumberFormat.Arabic, null).ShouldBe("3");
    }
}
