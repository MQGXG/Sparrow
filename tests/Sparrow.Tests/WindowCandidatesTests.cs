using Avalonia;
using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>截图悬停候选：命中测试与 Tab 循环。</summary>
public sealed class WindowCandidatesTests
{
    private static WindowCandidate Candidate(int id, string title, PixelRect bounds) =>
        new(new IntPtr(id), title, bounds, id);

    [Fact]
    public void 命中_返回最上层包含该点的窗口()
    {
        var front = Candidate(1, "前", new PixelRect(0, 0, 200, 200));
        var behind = Candidate(2, "后", new PixelRect(0, 0, 400, 400));

        WindowCandidates.Pick([front, behind], new PixelPoint(50, 50)).ShouldBe(front);
        WindowCandidates.Pick([front, behind], new PixelPoint(350, 350)).ShouldBe(behind);
        WindowCandidates.Pick([front, behind], new PixelPoint(500, 500)).ShouldBeNull();
    }

    [Fact]
    public void Tab循环_从上到下再到隐藏()
    {
        var front = Candidate(1, "前", new PixelRect(0, 0, 200, 200));
        var behind = Candidate(2, "后", new PixelRect(0, 0, 400, 400));
        var candidates = new[] { front, behind };
        var point = new PixelPoint(50, 50);

        WindowCandidates.Next(candidates, point, null).ShouldBe(front);
        WindowCandidates.Next(candidates, point, front).ShouldBe(behind);
        WindowCandidates.Next(candidates, point, behind).ShouldBeNull();
        WindowCandidates.Next(candidates, point, null).ShouldBe(front);
    }

    [Fact]
    public void Tab循环_点不在任何窗口时返回空()
    {
        var front = Candidate(1, "前", new PixelRect(0, 0, 100, 100));

        WindowCandidates.Next([front], new PixelPoint(200, 200), null).ShouldBeNull();
    }
}
