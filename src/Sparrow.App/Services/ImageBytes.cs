using SkiaSharp;

namespace Sparrow.App.Services;

/// <summary>
/// 图片字节的小工具集。宿主只认 PNG（剪贴板、预览），插件给的可能是 JPEG/WebP。
/// </summary>
public static class ImageBytes
{
    /// <summary>
    /// 任意常见格式（PNG / JPEG / WebP / BMP / GIF）的字节转成 PNG。
    /// 解不出图像时返回 null，调用方决定怎么提示。
    /// </summary>
    public static byte[]? ToPng(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            return null;

        try
        {
            using var bitmap = SKBitmap.Decode(data.Span);
            if (bitmap is null)
                return null;

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
