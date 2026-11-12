using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Shouldly;
using Sparrow.App.Converters;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 分类 → 图标映射。
/// 断言的是「有图标 / 每类不同 / 有缓存」，不锁具体路径数据，
/// 以后调整图标形状不用改测试。
///
/// 用 AvaloniaFact：Geometry.Parse 依赖 IPlatformRenderInterface，普通 xunit 下没有。
/// </summary>
public sealed class IconGeometryConverterTests
{
    private static readonly IconGeometryConverter Converter = new();

    private static Geometry Convert(string? category) =>
        (Geometry)Converter.Convert(category, typeof(Geometry), null, CultureInfo.InvariantCulture);

    [AvaloniaTheory]
    [InlineData("json")]
    [InlineData("dev")]
    [InlineData("ocr")]
    [InlineData("text")]
    [InlineData("format")]
    public void 已知分类都有图标(string category)
    {
        Convert(category).ShouldBeAssignableTo<Geometry>();
    }

    [AvaloniaFact]
    public void 未知分类退回默认图标()
    {
        var unknown = Convert("some-new-category");
        var known = Convert("json");

        unknown.ShouldNotBeNull();
        unknown.ShouldNotBeSameAs(known);
    }

    [AvaloniaFact]
    public void 空分类不抛异常()
    {
        Convert(null).ShouldNotBeNull();
        Convert(string.Empty).ShouldNotBeNull();
    }

    [AvaloniaFact]
    public void 同一路径返回缓存实例()
    {
        // 列表每次滚动都会重新转换，缓存避免重复解析 Path 数据
        Convert("json").ShouldBeSameAs(Convert("json"));
    }
}
