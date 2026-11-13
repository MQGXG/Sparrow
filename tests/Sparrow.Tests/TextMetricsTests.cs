using Shouldly;
using Sparrow.App.ViewModels;
using Xunit;

namespace Sparrow.Tests;

/// <summary>结果区字数/行数统计。</summary>
public sealed class TextMetricsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("abc", "3 字 · 1 行")]
    [InlineData("a\nb", "2 字 · 2 行")]
    [InlineData("a\r\nb\r\n", "2 字 · 3 行")]
    [InlineData("中文\n第二行", "5 字 · 2 行")]
    public void 统计字数与行数(string? text, string expected) =>
        TextMetrics.Summarize(text).ShouldBe(expected);
}
