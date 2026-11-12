using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Shouldly;
using Sparrow.App.Controls;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 命令面板的搜索高亮。
/// 断言的是 Run 的拆分结果，不是颜色——颜色改 token 时测试不该跟着红。
/// </summary>
public sealed class HighlightTextBlockTests
{
    [AvaloniaFact]
    public void 命中片段被拆成独立Run()
    {
        var block = new HighlightTextBlock { SourceText = "JSON 格式化", Term = "js" };

        var runs = block.Inlines!.OfType<Run>().ToList();

        runs.Count.ShouldBe(2);
        runs[0].Text.ShouldBe("JS");       // 保留原文大小写
        runs[0].FontWeight.ShouldBe(FontWeight.SemiBold);
        runs[1].Text.ShouldBe("ON 格式化");
    }

    [AvaloniaFact]
    public void 多个命中都会高亮()
    {
        var block = new HighlightTextBlock { SourceText = "a-b-a", Term = "a" };

        block.Inlines!.OfType<Run>().Select(run => run.Text)
            .ShouldBe(["a", "-b-", "a"]);
    }

    [AvaloniaFact]
    public void 没有搜索词时只有一段()
    {
        var block = new HighlightTextBlock { SourceText = "JSON 格式化", Term = null };

        var runs = block.Inlines!.OfType<Run>().ToList();

        runs.Count.ShouldBe(1);
        runs[0].Text.ShouldBe("JSON 格式化");
        runs[0].FontWeight.ShouldBe(FontWeight.Normal);
    }

    [AvaloniaFact]
    public void 没有命中时只有一段()
    {
        var block = new HighlightTextBlock { SourceText = "JSON 格式化", Term = "zzz" };

        block.Inlines!.OfType<Run>().ShouldHaveSingleItem().Text.ShouldBe("JSON 格式化");
    }

    [AvaloniaFact]
    public void 修改搜索词会重建()
    {
        var block = new HighlightTextBlock { SourceText = "abc", Term = "a" };
        block.Inlines!.OfType<Run>().Count().ShouldBe(2);

        block.Term = "c";

        block.Inlines!.OfType<Run>().Select(run => run.Text).ShouldBe(["ab", "c"]);
    }
}
