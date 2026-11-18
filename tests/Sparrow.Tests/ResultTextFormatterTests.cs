using Shouldly;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 表格 / 文件列表结果的文本化。
/// 宿主结果区是纯文本，表格靠等宽对齐；这里锁定列宽与分隔线，
/// 避免以后改格式时连「中文按两列宽」也算错。
/// </summary>
public sealed class ResultTextFormatterTests
{
    [Fact]
    public void 表格_按显示宽度补齐并加分隔线()
    {
        var table = new TablePayload(
            ["名称", "值"],
            [["a", "1"]]);

        var text = ResultTextFormatter.Format(table);

        var lines = text.Split(Environment.NewLine);
        lines.Length.ShouldBe(3);

        // 「名称」两个 CJK 字符占 4 列 → 分隔线第一段 4 个破折号
        lines[1].ShouldBe("────  ──");
        lines[2].ShouldStartWith("a ");
        lines[2].ShouldEndWith("  1");
    }

    [Fact]
    public void 表格_多行时每列取最大宽度()
    {
        var table = new TablePayload(
            ["文件", "大小"],
            [
                ["a.txt", "5 B"],
                ["long-name.txt", "1.0 KB"]
            ]);

        var text = ResultTextFormatter.Format(table);

        text.ShouldContain("a.txt");
        text.ShouldContain("long-name.txt");
        text.ShouldContain("5 B");
        text.ShouldContain("1.0 KB");
    }

    [Fact]
    public void 表格_没有列时返回空串()
    {
        ResultTextFormatter.Format(TablePayload.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void 文件列表_数量加逐行路径()
    {
        var text = ResultTextFormatter.Format(new FileSetPayload([@"C:\a.png", @"C:\b.png"]));

        text.ShouldBe($"2 个文件{Environment.NewLine}- C:\\a.png{Environment.NewLine}- C:\\b.png");
    }

    [Fact]
    public void 文件列表_为空时给一句人话()
    {
        ResultTextFormatter.Format(FileSetPayload.Empty).ShouldBe("没有文件");
    }
}
