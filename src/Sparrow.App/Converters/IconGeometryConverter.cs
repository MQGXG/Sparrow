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
/// 和 Styles/Icons.axaml 共用同一套画法：24×24 画布、只用 stroke、线宽 1.5、
/// 圆头圆角、安全区 20×20。XAML 里按 icon-sm / icon-md / icon-lg 缩放使用。
/// </summary>
public sealed class IconGeometryConverter : IValueConverter
{
    /// <summary>XAML 里用 {x:Static} 直接引用，避免 DataTemplate 的 StaticResource 作用域问题。</summary>
    public static IconGeometryConverter Instance { get; } = new();

    /// <summary>未知分类：一个收边的方框，和工具卡的取景框语言一致。</summary>
    private const string DefaultPath =
        "M5.5 8A2.5 2.5 0 0 1 8 5.5h8A2.5 2.5 0 0 1 18.5 8v8a2.5 2.5 0 0 1-2.5 2.5H8A2.5 2.5 0 0 1 5.5 16z";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase)
    {
        // JSON：一对对称的花括号
        ["json"] = "M9.6 4.6a2 2 0 0 0-2 2v2.5a1.9 1.9 0 0 1-1.9 1.9 1.9 1.9 0 0 1 1.9 1.9v2.5a2 2 0 0 0 2 2 "
                 + "M14.4 4.6a2 2 0 0 1 2 2v2.5a1.9 1.9 0 0 0 1.9 1.9 1.9 1.9 0 0 0-1.9 1.9v2.5a2 2 0 0 1-2 2",

        // 开发工具：尖括号 + 斜杠
        ["dev"] = "M9 7.8 4.8 12 9 16.2 M15 7.8 19.2 12 15 16.2 M13.4 5.4l-2.8 13.2",

        // 图片识别：取景框 + 两行文本
        ["ocr"] = "M8 4.5H6.5A2 2 0 0 0 4.5 6.5V8 M16 4.5h1.5a2 2 0 0 1 2 2V8 "
                + "M19.5 16v1.5a2 2 0 0 1-2 2H16 M8 19.5H6.5a2 2 0 0 1-2-2V16 "
                + "M8.5 9.5h7 M8.5 13h4.5",

        // 文本处理：三行文本
        ["text"] = "M5.5 7h13 M5.5 12h13 M5.5 17h8",

        // 格式 / 压缩：三层叠片
        ["format"] = "M12 4 4.5 8 12 12l7.5-4z M4.5 12 12 16l7.5-4 M4.5 16 12 20l7.5-4",

        // 剪贴板：板夹 + 夹口
        ["clipboard"] = "M9 3.5h6a1 1 0 0 1 1 1v2H8v-2a1 1 0 0 1 1-1z "
                      + "M8 6.5H7A1.5 1.5 0 0 0 5.5 8v10.5A1.5 1.5 0 0 0 7 20h10a1.5 1.5 0 0 0 1.5-1.5V8A1.5 1.5 0 0 0 17 6.5h-1",

        // AI：四角星 + 小星
        ["ai"] = "M12 3.9 13.9 8.9 18.6 10.8 13.9 12.7 12 17.7 10.1 12.7 5.4 10.8 10.1 8.9z "
               + "M18.3 15.5 19.1 17.4 21 18.2 19.1 19 18.3 20.9 17.5 19 15.6 18.2 17.5 17.4z",

        // 图像：圆角相框 + 太阳 + 山
        ["image"] = "M5 7.5A2.5 2.5 0 0 1 7.5 5h9A2.5 2.5 0 0 1 19 7.5v9a2.5 2.5 0 0 1-2.5 2.5h-9A2.5 2.5 0 0 1 5 16.5z "
                  + "M8.9 10.7a1.4 1.4 0 1 0 0-2.8 1.4 1.4 0 0 0 0 2.8 "
                  + "M5 15.5l4.3-4.3 3.5 3.5 2.4-2.4 3.8 3.8",
        ["images"] = "M5 7.5A2.5 2.5 0 0 1 7.5 5h9A2.5 2.5 0 0 1 19 7.5v9a2.5 2.5 0 0 1-2.5 2.5h-9A2.5 2.5 0 0 1 5 16.5z "
                   + "M8.9 10.7a1.4 1.4 0 1 0 0-2.8 1.4 1.4 0 0 0 0 2.8 "
                   + "M5 15.5l4.3-4.3 3.5 3.5 2.4-2.4 3.8 3.8",

        // 数据：数据库柱体
        ["data"] = "M12 4.5c-4.1 0-7.5 1.3-7.5 2.9v9.2c0 1.6 3.4 2.9 7.5 2.9s7.5-1.3 7.5-2.9V7.4c0-1.6-3.4-2.9-7.5-2.9z "
                 + "M4.5 7.4c0 1.6 3.4 2.9 7.5 2.9s7.5-1.3 7.5-2.9 "
                 + "M4.5 12.5c0 1.6 3.4 2.9 7.5 2.9s7.5-1.3 7.5-2.9",

        // 文件：折角纸
        ["file"] = "M6.5 4.5h6.7L18 9.2v10.3A1.5 1.5 0 0 1 16.5 21h-9A1.5 1.5 0 0 1 6 19.5v-14a1 1 0 0 1 .5-1z "
                 + "M13.2 4.5v4.7H18 M9 13h6 M9 16.5h4",
        ["files"] = "M6.5 4.5h6.7L18 9.2v10.3A1.5 1.5 0 0 1 16.5 21h-9A1.5 1.5 0 0 1 6 19.5v-14a1 1 0 0 1 .5-1z "
                  + "M13.2 4.5v4.7H18 M9 13h6 M9 16.5h4",

        // 窗口：标题栏 + 内容区
        ["window"] = "M4.5 6.5A2 2 0 0 1 6.5 4.5h11a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2h-11a2 2 0 0 1-2-2z "
                   + "M4.5 9h15 M7.2 6.75h1.1"
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
