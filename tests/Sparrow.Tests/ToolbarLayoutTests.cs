using Shouldly;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>工具条布局：默认回落、容错、隐藏、入口和分组顺序。</summary>
public sealed class ToolbarLayoutTests
{
    [Fact]
    public void 布局_空配置回落默认()
    {
        var layout = ToolbarLayout.Parse(null, null);

        layout.Positions.Count.ShouldBe(8);
        layout.Hidden.ShouldBeEmpty();
        layout.SerializePositions()[0].ShouldBe("Diamond|Ellipse|Bracket|Rectangle");
        layout.SerializePositions()[^1].ShouldBe("Number|Watermark|Bubble|Text");
    }

    [Fact]
    public void 布局_损坏或未知配置回落默认()
    {
        var layout = ToolbarLayout.Parse(["???", "", "  "], ["not-a-tool"]);

        layout.SerializePositions().ShouldBe(ToolbarLayout.Default().SerializePositions());
        layout.Hidden.ShouldBeEmpty();
    }

    [Fact]
    public void 布局_未知和重复id被跳过_缺失工具算隐藏()
    {
        var layout = ToolbarLayout.Parse(
            ["Ellipse|???|Ellipse|Rectangle", "Bracket|Rectangle"], null);

        layout.Positions.Count.ShouldBe(2);
        layout.Positions[0].ShouldBe(
            [AnnotationTool.Ellipse, AnnotationTool.Rectangle, AnnotationTool.Diamond]);
        layout.Positions[1].ShouldBe([AnnotationTool.Bracket]);

        layout.IsVisible(AnnotationTool.Rectangle).ShouldBeTrue();
        layout.IsVisible(AnnotationTool.Arrow).ShouldBeFalse();
        layout.Hidden.ShouldContain(AnnotationTool.Arrow);
    }

    [Fact]
    public void 布局_隐藏状态可序列化往返()
    {
        var layout = ToolbarLayout.Default();
        layout.Toggle(AnnotationTool.Eraser);
        layout.Toggle(AnnotationTool.Mosaic);

        var parsed = ToolbarLayout.Parse(
            layout.SerializePositions(), layout.SerializeHidden());

        parsed.IsVisible(AnnotationTool.Eraser).ShouldBeFalse();
        parsed.IsVisible(AnnotationTool.Mosaic).ShouldBeFalse();
        parsed.IsVisible(AnnotationTool.Rectangle).ShouldBeTrue();
    }

    [Fact]
    public void 布局_设为入口把工具移到槽位末尾()
    {
        var layout = ToolbarLayout.Default();

        layout.SetEntry(AnnotationTool.Ellipse);

        layout.Positions[0].ShouldBe(
            [AnnotationTool.Diamond, AnnotationTool.Bracket, AnnotationTool.Rectangle,
                AnnotationTool.Ellipse]);
    }

    [Fact]
    public void 布局_分组顺序可以交换()
    {
        var layout = ToolbarLayout.Default();

        layout.MovePosition(0, 1);

        layout.Positions[0].ShouldBe(ToolbarLayout.Groups[1].ToList());
        layout.Positions[1].ShouldBe(ToolbarLayout.Groups[0].ToList());

        // 越界不动
        layout.MovePosition(0, -1);
        layout.Positions[0].ShouldBe(ToolbarLayout.Groups[1].ToList());
    }
}
