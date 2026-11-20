using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Sparrow.App.Services;

namespace Sparrow.App.Converters;

/// <summary>
/// 把预览 PNG 字节转成 Avalonia 位图；ViewModel 不引用 Avalonia 类型。
///
/// 三档由 <see cref="ConverterParameter"/> 决定，不传参走原尺寸（保持既有行为不变）：
/// · <c>thumb</c>  列表缩略图 → 解码到 240px 高 + 按字节 LRU
/// · <c>detail</c> 详情大图   → 解码到 800px 宽 + 按字节 LRU
/// · 不传参       原尺寸     → 不缓存，调用方负责释放
///
/// 原来这里是每次都 <c>new Bitmap(stream)</c>：一张 4K 截图解码占 31.6MB，
/// 而列表显示只有 120px 高，且 native 内存不归 GC 管、从不释放——
/// 这是工作集涨到 450MB 的主因。
/// </summary>
public sealed class ByteArrayToBitmapConverter : IValueConverter
{
    public static ByteArrayToBitmapConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
            return null;

        return (parameter as string) switch
        {
            "thumb" => ImageCache.Instance.GetThumbnail(bytes),
            "detail" => ImageCache.Instance.GetDetail(bytes),
            _ => ImageCache.DecodeOriginal(bytes),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
