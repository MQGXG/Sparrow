using System.Globalization;
using Shouldly;
using Sparrow.App.Converters;
using Xunit;

namespace Sparrow.Tests;

/// <summary>类型 chip 的图标映射：几何内置，六个 Kind* 常量都要有图。</summary>
public sealed class ClipboardKindIconConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData("全部")]
    [InlineData("文本")]
    [InlineData("链接")]
    [InlineData("图片")]
    [InlineData("文件")]
    [InlineData("收藏")]
    public void 六个类型都有图标(string name)
    {
        var geometry = ClipboardKindIconConverter.Instance
            .Convert(name, typeof(object), null, Culture);

        geometry.ShouldNotBeNull($"{name} 必须有对应图标");
    }

    [Fact]
    public void 未知类型返回空_标签chip不画图标()
    {
        ClipboardKindIconConverter.Instance
            .Convert("自定义标签", typeof(object), null, Culture)
            .ShouldBeNull("标签 chip 走另一个模板，不该拿到类型图标");

        ClipboardKindIconConverter.Instance
            .Convert(null, typeof(object), null, Culture)
            .ShouldBeNull();
    }

    [Fact]
    public void 每次都新建几何_不跨线程复用同一实例()
    {
        var first = ClipboardKindIconConverter.Instance.Convert("文本", typeof(object), null, Culture);
        var second = ClipboardKindIconConverter.Instance.Convert("文本", typeof(object), null, Culture);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();

        // Geometry.PlatformImpl 是 Skia 对象，带线程亲和性：
        // 在 xUnit 线程池里创建、在 UI 线程 Measure 会抛 VerifyAccess，所以绝不能缓存实例
        ReferenceEquals(first, second).ShouldBeFalse();
    }
}
