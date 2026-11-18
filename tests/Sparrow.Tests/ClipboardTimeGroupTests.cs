using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>时间分叠的边界：今天优先，其次 3 天、7 天，再往后是更早。</summary>
public sealed class ClipboardTimeGroupTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void 今天_按本地日期判断()
    {
        ClipboardTimeGroup.Resolve(Now.AddHours(-2), Now).ShouldBe(ClipboardTimeGroup.Today);
        ClipboardTimeGroup.Resolve(new DateTimeOffset(2026, 11, 18, 0, 5, 0, Now.Offset), Now)
            .ShouldBe(ClipboardTimeGroup.Today);
    }

    [Fact]
    public void 三天内与一周内()
    {
        ClipboardTimeGroup.Resolve(Now.AddDays(-2), Now).ShouldBe(ClipboardTimeGroup.ThreeDays);
        ClipboardTimeGroup.Resolve(Now.AddDays(-6), Now).ShouldBe(ClipboardTimeGroup.Week);
    }

    [Fact]
    public void 超过一周_更早()
    {
        ClipboardTimeGroup.Resolve(Now.AddDays(-8), Now).ShouldBe(ClipboardTimeGroup.Older);
    }

    [Fact]
    public void 跨时区的昨天_不归到今天()
    {
        var createdAt = new DateTimeOffset(2026, 11, 17, 23, 30, 0, TimeSpan.FromHours(8));
        ClipboardTimeGroup.Resolve(createdAt, Now).ShouldBe(ClipboardTimeGroup.ThreeDays);
    }
}
