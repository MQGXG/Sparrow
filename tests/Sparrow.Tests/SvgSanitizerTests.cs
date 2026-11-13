using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

public sealed class SvgSanitizerTests
{
    [Fact]
    public void 合法SVG_保留图形()
    {
        var result = SvgSanitizer.Sanitize(
            """<svg viewBox="0 0 10 10"><rect width="10" height="10" fill="red"/></svg>""");

        result.Success.ShouldBeTrue();
        result.Svg.ShouldNotBeNull().ShouldContain("<rect");
    }

    [Fact]
    public void 脚本与事件属性_被删除()
    {
        var result = SvgSanitizer.Sanitize(
            """<svg viewBox="0 0 10 10" onload="alert(1)"><script>alert(1)</script><rect width="10" height="10" onclick="x()"/></svg>""");

        result.Success.ShouldBeTrue();
        result.Svg.ShouldNotBeNull().ShouldNotContain("script");
        result.Svg.ShouldNotContain("onload");
        result.Svg.ShouldNotContain("onclick");
    }

    [Fact]
    public void 外链资源_被删除()
    {
        var result = SvgSanitizer.Sanitize(
            """<svg viewBox="0 0 10 10"><image href="https://example.com/a.png" width="10" height="10"/><use href="file:///c:/secret.svg"/></svg>""");

        result.Success.ShouldBeTrue();
        result.Svg.ShouldNotBeNull().ShouldNotContain("https://example.com");
        result.Svg.ShouldNotContain("file:///");
        result.Svg.ShouldNotContain("<image");
    }

    [Fact]
    public void 根元素不是SVG_失败()
    {
        SvgSanitizer.Sanitize("<html><body>hi</body></html>").Success.ShouldBeFalse();
    }

    [Fact]
    public void 空内容_失败()
    {
        SvgSanitizer.Sanitize("   ").Success.ShouldBeFalse();
    }

    [Fact]
    public void 外部样式_被删除()
    {
        var result = SvgSanitizer.Sanitize(
            """<svg viewBox="0 0 10 10"><rect style="fill:url(http://evil/x)" width="10" height="10"/></svg>""");

        result.Success.ShouldBeTrue();
        result.Svg.ShouldNotBeNull().ShouldNotContain("http://evil");
    }
}
