using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sparrow.App.Converters;

/// <summary>
/// 剪贴板类型 chip → 线性图标。
///
/// 和 <see cref="IconGeometryConverter"/> 同一个模式：几何内置在这里并缓存，
/// 不在运行时查资源——Avalonia 12 的 Application 没有 FindResource，
/// 而 DataTemplate 里的 StaticResource 也拿不到 App 级字典。
///
/// 几何与 Styles/Icons.axaml 里的 IconClipboard / IconText / IconLink /
/// IconImage / IconFile / IconStar 逐字相同，改图标时两处要一起改。
/// 映射按 chip 的 Name，而 Name 就是 ClipboardHistoryViewModel 里的 Kind* 常量
/// （全部 / 文本 / 链接 / 图片 / 文件 / 收藏），不是用户输入，按字符串匹配是安全的。
/// 未知类型返回 null，Path 不画；标签 chip 走另一个模板，不受影响。
/// </summary>
public sealed class ClipboardKindIconConverter : IValueConverter
{
    /// <summary>XAML 里用 {x:Static} 直接引用，避免 DataTemplate 的 StaticResource 作用域问题。</summary>
    public static ClipboardKindIconConverter Instance { get; } = new();

    /// <summary>
    /// 只缓存路径字符串，不缓存 Geometry 本身：
    /// <c>Geometry.PlatformImpl</c> 是 Skia 对象，带线程亲和性，
    /// 在 A 线程创建、B 线程 Measure 会直接抛 VerifyAccess。
    /// 每次 Parse 一个 24×24 的几何是纯 CPU，代价可以忽略。
    /// </summary>
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["全部"] = "M9 3.5h6a1 1 0 0 1 1 1v2H8v-2a1 1 0 0 1 1-1z M8 6.5H7A1.5 1.5 0 0 0 5.5 8v10.5A1.5 1.5 0 0 0 7 20h10a1.5 1.5 0 0 0 1.5-1.5V8A1.5 1.5 0 0 0 17 6.5h-1",
        ["文本"] = "M7 18 12 6l5 12 M9.2 14h5.6",
        ["链接"] = "M10.5 13.5a4.5 4.5 0 0 0 6.36 0l3-3a4.5 4.5 0 0 0-6.36-6.36l-1.5 1.5 M13.5 10.5a4.5 4.5 0 0 0-6.36 0l-3 3a4.5 4.5 0 0 0 6.36 6.36l1.5-1.5",
        ["图片"] = "M4.5 6.5A1.5 1.5 0 0 1 6 5h12a1.5 1.5 0 0 1 1.5 1.5v11A1.5 1.5 0 0 1 18 19H6a1.5 1.5 0 0 1-1.5-1.5z M4.5 15.5 9 11l3.5 3.5L15 12l4.5 4.5 M9 9.5a1.2 1.2 0 1 0 0-2.4 1.2 1.2 0 0 0 0 2.4z",
        ["文件"] = "M13 3.5H7A1.5 1.5 0 0 0 5.5 5v14A1.5 1.5 0 0 0 7 20.5h10a1.5 1.5 0 0 0 1.5-1.5V9 M13 3.5V9h5.5",
        ["收藏"] = "M12 3.9 13.9 9.7 20 9.7 15 13.3 16.9 19.1 12 15.5 7.1 19.1 9 13.3 4 9.7 10.1 9.7Z",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string name && Paths.TryGetValue(name, out var path)
            ? StreamGeometry.Parse(path)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("类型 chip 的图标是单向的");
}
