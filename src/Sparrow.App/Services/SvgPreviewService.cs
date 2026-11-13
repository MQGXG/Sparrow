using System.Text;
using SkiaSharp;
using Svg.Skia;

namespace Sparrow.App.Services;

/// <summary>
/// 把清洗后的 SVG 渲染成 PNG，供工作区预览。
///
/// 渲染放在后台线程，限制最大边长；失败返回 null，由调用方回退到源码。
/// </summary>
public sealed class SvgPreviewService
{
    public const int MaxDimension = 1024;

    public Task<byte[]?> RenderAsync(string svg, CancellationToken cancellationToken) =>
        Task.Run(() => Render(svg, cancellationToken), cancellationToken);

    private static byte[]? Render(string svg, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            var document = new SKSvg();

            if (document.Load(stream) is null || document.Picture is null)
                return null;

            var picture = document.Picture;
            var bounds = picture.CullRect;

            if (bounds.Width <= 0 || bounds.Height <= 0)
                return null;

            var scale = Math.Min(1.0, MaxDimension / Math.Max(bounds.Width, bounds.Height));
            var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));

            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();

            cancellationToken.ThrowIfCancellationRequested();

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data?.ToArray();
        }
        catch
        {
            // 渲染失败不是致命错误：界面回退到源码
            return null;
        }
    }
}
