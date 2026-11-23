using SkiaSharp;

namespace Sparrow.App.Services;

/// <summary>按行裁掉滚动长图的头尾。</summary>
internal static class ScrollImageTrimmer
{
    /// <summary>裁掉顶部 / 底部各若干行；没有裁剪或解码失败时原样返回。</summary>
    public static byte[] TrimPng(byte[] png, int trimTopRows, int trimBottomRows)
    {
        if (png.Length == 0 || (trimTopRows <= 0 && trimBottomRows <= 0))
            return png;

        try
        {
            using var source = SKBitmap.Decode(png);
            if (source is null || source.Height <= 0 || source.Width <= 0)
                return png;

            var top = Math.Clamp(trimTopRows, 0, source.Height - 1);
            var bottom = Math.Clamp(trimBottomRows, 0, source.Height - 1 - top);
            var height = source.Height - top - bottom;

            if (height <= 0 || (top == 0 && bottom == 0))
                return png;

            using var subset = new SKBitmap();

            if (!source.ExtractSubset(subset, new SKRectI(0, top, source.Width, top + height)))
                return png;

            using var image = SKImage.FromBitmap(subset);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            return data?.ToArray() ?? png;
        }
        catch
        {
            return png;
        }
    }
}
