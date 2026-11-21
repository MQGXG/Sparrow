using Shouldly;
using Sparrow.Plugin.Ocr;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 表格结构 + 文字回填：SLANet 只给单元格框，文字靠几何包含关系分配，
/// 这里覆盖三种 &lt;td&gt; 形态、排序和转义。
/// </summary>
public sealed class TableHtmlBuilderTests
{
    private static readonly string[] TwoByTwoTags = ["<tr>", "<td></td>", "<td></td>", "</tr>", "<tr>", "<td></td>", "<td></td>", "</tr>"];

    private static readonly TableGridCell[] TwoByTwoCells =
    [
        new(0, 0, 100, 50),
        new(100, 0, 100, 50),
        new(0, 50, 100, 50),
        new(100, 50, 100, 50)
    ];

    [Fact]
    public void 文字按中心点回填到对应格子()
    {
        var html = TableHtmlBuilder.Compose(
            TwoByTwoTags,
            TwoByTwoCells,
            [
                new TableTextBlock("名称", 10, 10, 40, 20),
                new TableTextBlock("数量", 110, 10, 40, 20),
                new TableTextBlock("键盘", 10, 60, 40, 20),
                new TableTextBlock("2", 110, 60, 10, 20)
            ]);

        html.ShouldBe(
            "<table><tr><td>名称</td><td>数量</td></tr><tr><td>键盘</td><td>2</td></tr></table>");
    }

    [Fact]
    public void 同一格多行文字按阅读顺序连接()
    {
        var html = TableHtmlBuilder.Compose(
            ["<tr>", "<td></td>", "</tr>"],
            [new TableGridCell(0, 0, 200, 100)],
            [
                new TableTextBlock("第二行", 10, 60, 60, 20),
                new TableTextBlock("第一行", 10, 15, 60, 20)
            ]);

        html.ShouldBe("<table><tr><td>第一行 第二行</td></tr></table>");
    }

    [Fact]
    public void 表格外的文字被忽略()
    {
        var html = TableHtmlBuilder.Compose(
            ["<tr>", "<td></td>", "</tr>"],
            [new TableGridCell(0, 0, 100, 50)],
            [
                new TableTextBlock("表内", 10, 10, 40, 20),
                new TableTextBlock("表外标题", 10, 200, 80, 20)
            ]);

        html.ShouldBe("<table><tr><td>表内</td></tr></table>");
    }

    [Fact]
    public void 中心点在框外时按重叠面积归属()
    {
        // 文字框压在格子边界上，中心点落到所有格子之外，靠重叠面积恢复归属
        var html = TableHtmlBuilder.Compose(
            ["<tr>", "<td></td>", "<td></td>", "</tr>"],
            [new TableGridCell(0, 0, 100, 50), new TableGridCell(100, 0, 100, 50)],
            [new TableTextBlock("贴边文字", 60, 40, 60, 30)]);

        html.ShouldBe("<table><tr><td>贴边文字</td><td></td></tr></table>");
    }

    [Fact]
    public void 带属性的td_文字插在闭合标签前()
    {
        // 带 colspan 的格子：Sdcb 拆成 "<td" + 属性 + ">" + "</td>"
        var html = TableHtmlBuilder.Compose(
            ["<tr>", "<td", " colspan=\"2\"", ">", "</td>", "</tr>"],
            [new TableGridCell(0, 0, 200, 50)],
            [new TableTextBlock("合并", 10, 10, 40, 20)]);

        html.ShouldBe("<table><tr><td colspan=\"2\">合并</td></tr></table>");
    }

    [Fact]
    public void 文字里的尖括号被转义()
    {
        var html = TableHtmlBuilder.Compose(
            ["<tr>", "<td></td>", "</tr>"],
            [new TableGridCell(0, 0, 200, 50)],
            [new TableTextBlock("a < b & c", 10, 10, 100, 20)]);

        html.ShouldBe("<table><tr><td>a &lt; b &amp; c</td></tr></table>");
    }

    [Fact]
    public void 没有文字时保留空单元格()
    {
        var html = TableHtmlBuilder.Compose(
            TwoByTwoTags,
            TwoByTwoCells,
            []);

        html.ShouldBe(
            "<table><tr><td></td><td></td></tr><tr><td></td><td></td></tr></table>");
    }
}
