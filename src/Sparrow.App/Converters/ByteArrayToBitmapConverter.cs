using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Sparrow.App.Converters;

/// <summary>把预览 PNG 字节转成 Avalonia 位图；ViewModel 不引用 Avalonia 类型。</summary>
public sealed class ByteArrayToBitmapConverter : IValueConverter
{
    public static ByteArrayToBitmapConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
            return null;

        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
