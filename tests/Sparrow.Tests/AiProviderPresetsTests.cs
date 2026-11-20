using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>模型提供方预设：设置页直接拿来填空，写错一个字用户就连不上。</summary>
public sealed class AiProviderPresetsTests
{
    [Fact]
    public void 预设的端点都是合法的HTTP地址()
    {
        foreach (var provider in AiProviders.All.Where(item => item.Key != "custom"))
        {
            Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
                .ShouldBeTrue($"{provider.Key} 的端点不合法：{provider.BaseUrl}");

            uri!.Scheme.ShouldBeOneOf("http", "https");
        }
    }

    [Fact]
    public void 预设的名称键值模型都不为空且键唯一()
    {
        var presets = AiProviders.All;

        presets.Select(provider => provider.Key).Distinct().Count().ShouldBe(presets.Count);

        foreach (var provider in presets)
        {
            provider.Name.ShouldNotBeNullOrWhiteSpace();
            provider.Key.ShouldNotBeNullOrWhiteSpace();
        }

        // 「自定义」允许留空端点和模型，其余必须预填
        foreach (var provider in presets.Where(item => item.Key != "custom"))
            provider.Model.ShouldNotBeNullOrWhiteSpace($"{provider.Key} 缺默认模型");
    }

    [Fact]
    public void 预设_按Key查找不区分大小写()
    {
        AiProviders.Find("SILICONFLOW").ShouldNotBeNull().Name.ShouldBe("硅基流动");
        AiProviders.Find("no-such-provider").ShouldBeNull();
    }
}
