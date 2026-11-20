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
    public void 同一个类型复用同一份几何()
    {
        var first = ClipboardKindIconConverter.Instance.Convert("文本", typeof(object), null, Culture);
        var second = ClipboardKindIconConverter.Instance.Convert("文本", typeof(object), null, Culture);

        ReferenceEquals(first, second).ShouldBeTrue("几何要缓存，列表滚动时不该反复 Parse");
    }
}
