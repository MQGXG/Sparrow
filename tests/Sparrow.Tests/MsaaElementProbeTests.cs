using Avalonia;
using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// MSAA 兜底探测：只验证「拿不到就安静返回」，不依赖真实应用的无障碍树。
/// </summary>
public sealed class MsaaElementProbeTests
{
    [Fact]
    public async Task 无效窗口_返回空而不是抛异常()
    {
        using var probe = new MsaaElementProbe();

        var candidate = await probe.ProbeAsync(
            IntPtr.Zero,
            new PixelPoint(10, 10),
            TimeSpan.FromMilliseconds(300));

        candidate.ShouldBeNull();
    }

    [Fact]
    public async Task 释放后_探测直接返回空()
    {
        var probe = new MsaaElementProbe();
        probe.Dispose();

        var candidate = await probe.ProbeAsync(
            new IntPtr(0x1234),
            new PixelPoint(10, 10),
            TimeSpan.FromMilliseconds(100));

        candidate.ShouldBeNull();
    }
}
