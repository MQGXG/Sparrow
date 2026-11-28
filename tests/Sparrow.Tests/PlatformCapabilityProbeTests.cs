using Shouldly;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 能力探测：插件声明 Requires，宿主判断当前环境是否满足。
/// 不满足时返回可读原因，UI 据此灰显并禁止执行。
/// </summary>
public sealed class PlatformCapabilityProbeTests
{
    private sealed class FakeAi(bool available) : IAiClient
    {
        public bool IsAvailable { get; } = available;

        public string? ModelName => null;

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHttp(bool available) : IHttpClientAccess
    {
        public bool IsAvailable { get; } = available;

        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void 没有能力要求时_可用()
    {
        var probe = new HostPlatformCapabilityProbe();

        probe.DescribeMissing(PlatformCapability.None).ShouldBeNull();
    }

    [Fact]
    public void 基础宿主能力_全部可用()
    {
        var probe = new HostPlatformCapabilityProbe();
        var baseline = PlatformCapability.Clipboard
                       | PlatformCapability.ClipboardMonitor
                       | PlatformCapability.Screenshot
                       | PlatformCapability.Selection
                       | PlatformCapability.GlobalHotkey
                       | PlatformCapability.SvgRender;

        probe.DescribeMissing(baseline).ShouldBeNull();
    }

    [Fact]
    public void 未配置AI时_返回可读原因()
    {
        var probe = new HostPlatformCapabilityProbe();

        var reason = probe.DescribeMissing(PlatformCapability.Ai);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("AI");
    }

    [Fact]
    public void 配置AI后_满足AI能力()
    {
        var probe = new HostPlatformCapabilityProbe(new FakeAi(available: true));

        probe.DescribeMissing(PlatformCapability.Ai).ShouldBeNull();
    }

    [Fact]
    public void 网络未启用时_返回可读原因()
    {
        var probe = new HostPlatformCapabilityProbe();

        var reason = probe.DescribeMissing(PlatformCapability.Network);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("网络");
    }

    [Fact]
    public void 多个能力缺失时_原因包含每一项()
    {
        var probe = new HostPlatformCapabilityProbe(
            new FakeAi(available: false),
            new FakeHttp(available: false));

        var reason = probe.DescribeMissing(
            PlatformCapability.Ai | PlatformCapability.Network);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("AI");
        reason.ShouldContain("网络");
    }
}
