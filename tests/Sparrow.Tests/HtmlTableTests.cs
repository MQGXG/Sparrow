using Shouldly;
using Sparrow.Plugin.Ocr;
using Xunit;

namespace Sparrow.Tests;

/// <summary>HTML 表格 → Markdown：模型只出 HTML，转换必须确定、可测。</summary>
public sealed class HtmlTableTests
{
    [Fact]
    public void 基本表格_首行当表头并补分隔行()
    {
        const string html = """
            <table>
              <tr><th>姓名</th><th>数量</th></tr>
              <tr><td>张三</td><td>2</td></tr>
              <tr><td>李四</td><td>1</td></tr>
            </table>
            """;

        var markdown = HtmlTable.ToMarkdown(html);

        markdown.ShouldNotBeNull();
        markdown.ShouldBe("""
            | 姓名 | 数量 |
            | --- | --- |
            | 张三 | 2 |
            | 李四 | 1 |
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void 合并单元格_展开成空格子()
    {
        const string html = """
            <table>
              <tr><th>A</th><th>B</th><th>C</th></tr>
              <tr><td colspan="2">合并</td><td>x</td></tr>
            </table>
            """;

        var markdown = HtmlTable.ToMarkdown(html);

        markdown.ShouldNotBeNull();
        markdown.ShouldContain("| 合并 |  | x |");
    }

    [Fact]
    public void 实体与换行被清理_竖线被转义()
    {
        const string html = """
            <table>
              <tr><th>说明</th></tr>
              <tr><td>a &amp; b<br>第二行</td></tr>
              <tr><td>1|2</td></tr>
            </table>
            """;

        var markdown = HtmlTable.ToMarkdown(html);

        markdown.ShouldNotBeNull();
        markdown.ShouldContain("a & b 第二行");
        markdown.ShouldContain(@"1\|2");
    }

    [Fact]
    public void 没有表格时返回空_让调用方自己决定()
    {
        HtmlTable.ToMarkdown("这里没有表格").ShouldBeNull();
        HtmlTable.ToMarkdown(string.Empty).ShouldBeNull();
    }
}
