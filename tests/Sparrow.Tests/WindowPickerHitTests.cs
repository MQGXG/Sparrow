using Shouldly;
using Sparrow.App.Overlays;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 窗口拾取的命中判断（纯函数）：验证「只认光标点下的窗口」这条规则。
/// 覆盖从托盘菜单启动时光标停在托盘 / 桌面导致误选不相干窗口的历史缺陷。
/// </summary>
public sealed class WindowPickerHitTests
{
    private const uint CurrentProcess = 100;
    private const uint OtherProcess = 200;

    private static WindowPickerApi.Rect Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };

    private static WindowPickerApi.WindowCandidate Candidate(
        nint handle,
        uint processId = OtherProcess,
        bool visible = true,
        bool minimized = false,
        bool shell = false,
        WindowPickerApi.Rect bounds = default) =>
        new(handle, processId, visible, minimized, shell,
            bounds.Right == 0 && bounds.Bottom == 0 ? Rect(0, 0, 800, 600) : bounds);

    [Fact]
    public void 光标下没有窗口_返回0而不是随便挑一个()
    {
        var candidates = new[]
        {
            Candidate(11, bounds: Rect(0, 0, 400, 300))
        };

        WindowPickerApi.SelectWindowAt(
            new WindowPickerApi.Point { X = 900, Y = 700 }, CurrentProcess, candidates)
            .ShouldBe((nint)0);
    }

    [Fact]
    public void 光标在托盘区_不能穿透到不覆盖光标的窗口()
    {
        var candidates = new[]
        {
            Candidate(11, shell: true, bounds: Rect(0, 0, 1920, 1080)),
            Candidate(12, bounds: Rect(100, 100, 500, 400))
        };

        WindowPickerApi.SelectWindowAt(
            new WindowPickerApi.Point { X = 1800, Y = 1050 }, CurrentProcess, candidates)
            .ShouldBe((nint)0);
    }

    [Fact]
    public void 本进程窗口被跳过_取下一个覆盖光标的外部窗口()
    {
        var candidates = new[]
        {
            Candidate(11, CurrentProcess, bounds: Rect(0, 0, 1920, 1080)),
            Candidate(12, bounds: Rect(100, 100, 500, 400))
        };

        WindowPickerApi.SelectWindowAt(
            new WindowPickerApi.Point { X = 200, Y = 200 }, CurrentProcess, candidates)
            .ShouldBe((nint)12);
    }

    [Fact]
    public void 多个窗口重叠_取Z序最靠前的那个()
    {
        var candidates = new[]
        {
            Candidate(11, bounds: Rect(100, 100, 500, 400)),
            Candidate(12, bounds: Rect(100, 100, 500, 400))
        };

        WindowPickerApi.SelectWindowAt(
            new WindowPickerApi.Point { X = 200, Y = 200 }, CurrentProcess, candidates)
            .ShouldBe((nint)11);
    }

    [Fact]
    public void 不可见_最小化_无进程ID_零面积窗口都跳过()
    {
        var candidates = new[]
        {
            Candidate(11, visible: false, bounds: Rect(0, 0, 1920, 1080)),
            Candidate(12, minimized: true, bounds: Rect(0, 0, 1920, 1080)),
            Candidate(13, processId: 0, bounds: Rect(0, 0, 1920, 1080)),
            Candidate(14, bounds: Rect(10, 10, 10, 10)),
            Candidate(15, bounds: Rect(100, 100, 500, 400))
        };

        WindowPickerApi.SelectWindowAt(
            new WindowPickerApi.Point { X = 200, Y = 200 }, CurrentProcess, candidates)
            .ShouldBe((nint)15);
    }
}
