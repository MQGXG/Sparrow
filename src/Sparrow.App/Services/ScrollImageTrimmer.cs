using SkiaSharp;

namespace Sparrow.App.Services;

/// <summary>裁掉滚动长图的头尾：纵向按行、横向按列。</summary>
internal static class ScrollImageTrimmer
{
    /// <summary>
    /// 裁掉两端各若干行（横向为列）；没有裁剪或解码失败时原样返回。
    /// </summary>
    /// <param name="trimStart">纵向为顶部行数，横向为左侧列数。</param>
    /// <param name="trimEnd">纵向为底部行数，横向为右侧列数。</param>
    /// <param name="horizontal">true 表示按列裁剪。</param>
    public static byte[] TrimPng(
        byte[] png, int trimStart, int trimEnd, bool horizontal = false)
    {
        if (png.Length == 0 || (trimStart <= 0 && trimEnd <= 0))
            return png;

        try
        {
            using var source = SKBitmap.Decode(png);
            if (source is null || source.Height <= 0 || source.Width <= 0)
                return png;

            var extent = horizontal ? source.Width : source.Height;
            var start = Math.Clamp(trimStart, 0, extent - 1);
            var end = Math.Clamp(trimEnd, 0, extent - 1 - start);
            var length = extent - start - end;

            if (length <= 0 || (start == 0 && end == 0))
                return png;

            var region = horizontal
                ? new SKRectI(start, 0, start + length, source.Height)
                : new SKRectI(0, start, source.Width, start + length);

            using var subset = new SKBitmap();

            if (!source.ExtractSubset(subset, region))
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
