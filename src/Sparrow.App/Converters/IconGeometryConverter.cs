using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Sparrow.App.Converters;

/// <summary>
/// 分类 → 线性图标。
///
/// 图标是视觉标识，不是业务数据，所以不放进 ViewModel；
/// 用 Category（插件声明的静态元数据）做映射，未知分类退回默认图标，
/// 新插件不需要改宿主就能拿到一个可用的图标。
///
/// 统一 24×24 画布、stroke 1.5、圆头圆角，XAML 里缩到 18px 使用。
/// </summary>
public sealed class IconGeometryConverter : IValueConverter
{
    /// <summary>XAML 里用 {x:Static} 直接引用，避免 DataTemplate 的 StaticResource 作用域问题。</summary>
    public static IconGeometryConverter Instance { get; } = new();

    private const string DefaultPath = "M6 6h12v12H6z";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        // JSON：花括号
        ["json"] = "M9 4c-1.7 0-2 1.3-2 2v2c0 1.2-.8 2-2 2 1.2 0 2 .8 2 2v2c0 .7.3 2 2 2 "
                 + "M15 4c1.7 0 2 1.3 2 2v2c0 1.2.8 2 2 2-1.2 0-2 .8-2 2v2c0 .7-.3 2-2 2",

        // 开发工具：尖括号
        ["dev"] = "M9 8 5 12l4 4 M15 8l4 4-4 4",

        // 图片识别：扫描框 + 文本行
        ["ocr"] = "M4 8V6a2 2 0 0 1 2-2h2 M16 4h2a2 2 0 0 1 2 2v2 "
                + "M20 16v2a2 2 0 0 1-2 2h-2 M8 20H6a2 2 0 0 1-2-2v-2 "
                + "M8 10h8 M8 13h5",

        // 文本处理：文本行
        ["text"] = "M5 7h14 M5 12h14 M5 17h9",

        // 格式/压缩：层叠方块
        ["format"] = "M12 4 4 8l8 4 8-4-8-4z M4 12l8 4 8-4 M4 16l8 4 8-4"
    };

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        var path = key is not null && Paths.TryGetValue(key, out var known) ? known : DefaultPath;

        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
                return cached;

            var geometry = Geometry.Parse(path);
            Cache[path] = geometry;
            return geometry;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
