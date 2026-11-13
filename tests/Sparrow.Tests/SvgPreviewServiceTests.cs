using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

public sealed class SvgPreviewServiceTests
{
    [Fact]
    public async Task 简单SVG_渲染成PNG()
    {
        var service = new SvgPreviewService();

        var png = await service.RenderAsync(
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" fill="#E5A44C"/></svg>""",
            CancellationToken.None);

        png.ShouldNotBeNull();
        png!.Length.ShouldBeGreaterThan(20);
        png[0].ShouldBe((byte)0x89);
        png[1].ShouldBe((byte)0x50);
        png[2].ShouldBe((byte)0x4E);
        png[3].ShouldBe((byte)0x47);
    }

    [Fact]
    public async Task 非法SVG_返回null()
    {
        var service = new SvgPreviewService();

        var png = await service.RenderAsync("<svg><not-closed>", CancellationToken.None);

        png.ShouldBeNull();
    }
}
