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

    [Fact]
    public void 跨屏普通窗口_取交集并集()
    {
        var monitors = new[]
        {
            new PixelRect(-1920, 0, 1920, 1080),
            new PixelRect(0, 0, 2560, 1440)
        };
        var window = new PixelRect(-100, 20, 200, 120);

        var rects = WindowCandidates.SelectionRects(window, desktop: false, monitors);

        rects.ShouldBe([window]);
    }

    [Fact]
    public void 桌面宿主_按显示器拆分()
    {
        var monitors = new[]
        {
            new PixelRect(-1920, 0, 1920, 1080),
            new PixelRect(0, 0, 2560, 1440)
        };
        var desktop = new PixelRect(-1920, 0, 4480, 1440);

        var rects = WindowCandidates.SelectionRects(desktop, desktop: true, monitors);

        rects.ShouldBe(monitors);
    }

    [Fact]
    public void 窗口部分在屏外_裁剪到显示器()
    {
        var monitors = new[] { new PixelRect(0, 0, 1000, 800) };
        var window = new PixelRect(900, 100, 300, 200);

        var rects = WindowCandidates.SelectionRects(window, desktop: false, monitors);

        rects.ShouldBe([new PixelRect(900, 100, 100, 200)]);
    }

    [Fact]
    public void 完全不与显示器相交_没有候选()
    {
        var monitors = new[] { new PixelRect(0, 0, 1000, 800) };

        WindowCandidates.SelectionRects(
                new PixelRect(2000, 2000, 100, 100), desktop: false, monitors)
            .ShouldBeEmpty();
    }

    [Fact]
    public void 同一窗口拆到不同屏_视为不同候选()
    {
        var left = Candidate(1, "桌面", new PixelRect(0, 0, 100, 100));
        var right = Candidate(1, "桌面", new PixelRect(100, 0, 100, 100));

        left.ShouldNotBe(right);
    }
}
