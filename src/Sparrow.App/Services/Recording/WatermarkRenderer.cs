using System.Runtime.InteropServices;
using SkiaSharp;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 图文水印：一个「图片 + 文字」组合块，按相对布局画在 BGRA 缓冲区上。
///
/// 图片只在构造时解码一次；每帧只做缩放绘制，不重复读盘。
/// 图片解码失败时降级为纯文字，不中断录制。
/// </summary>
internal sealed class WatermarkRenderer : IDisposable
{
    private readonly WatermarkLayout _layout;
    private readonly SKTypeface _typeface;
    private readonly SKFont _font;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _shadowPaint;
    private readonly SKPaint _imagePaint;
    private readonly SKImage? _image;

    public WatermarkRenderer(WatermarkLayout layout)
    {
        _layout = layout;
        _typeface = SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default;
        _font = new SKFont(_typeface);

        var alpha = (byte)Math.Clamp(layout.Opacity * 255, 0, 255);

        _textPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, alpha),
            IsAntialias = true
        };

        _shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, (byte)(alpha * 0.6)),
            IsAntialias = true
        };

        _imagePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, alpha),
            IsAntialias = true
        };

        _image = LoadImage(layout.ImagePath);
    }

    public void Draw(byte[] pixels, int width, int height)
    {
        if (_layout.IsEmpty || pixels.Length < width * height * 4)
            return;

        var blockHeight = (float)Math.Clamp(height * _layout.Scale, 8, height * 0.35);
        var hasText = !string.IsNullOrWhiteSpace(_layout.Text);
        var hasImage = _image is not null;

        if (!hasText && !hasImage)
            return;

        var imageWidth = 0f;

        if (hasImage)
        {
            imageWidth = _image!.Width * blockHeight / _image.Height;
        }

        _font.Size = (float)Math.Clamp(blockHeight * 0.55 * _layout.TextScale, 6, height);

        var textWidth = hasText ? _font.MeasureText(_layout.Text) : 0f;
        var gap = hasImage && hasText ? blockHeight * 0.25f : 0f;
        var totalWidth = imageWidth + gap + textWidth;
        var totalHeight = Math.Max(blockHeight, hasText ? _font.Size : 0);

        var centerX = (float)(_layout.CenterX * width);
        var centerY = (float)(_layout.CenterY * height);
        var left = centerX - totalWidth / 2;
        var top = centerY - totalHeight / 2;

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            var info = new SKImageInfo(
                width,
                height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);

            using var surface = SKSurface.Create(
                info,
                handle.AddrOfPinnedObject(),
                width * 4);

            if (surface is null)
                return;

            var canvas = surface.Canvas;

            if (hasImage)
            {
                var target = new SKRect(
                    left,
                    top + (totalHeight - blockHeight) / 2,
                    left + imageWidth,
                    top + (totalHeight - blockHeight) / 2 + blockHeight);

                canvas.DrawImage(_image!, target, _imagePaint);
            }

            if (hasText)
            {
                var textX = left + imageWidth + gap;
                var baseline = top + totalHeight / 2 + _font.Size * 0.35f;

                canvas.DrawText(_layout.Text, textX + 1, baseline + 1, SKTextAlign.Left, _font, _shadowPaint);
                canvas.DrawText(_layout.Text, textX, baseline, SKTextAlign.Left, _font, _textPaint);
            }

            canvas.Flush();
        }
        finally
        {
            handle.Free();
        }
    }

    private static SKImage? LoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var bitmap = SKBitmap.Decode(path);

            return bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
        catch
        {
            // 图片格式不支持：降级为纯文字。
            return null;
        }
    }

    public void Dispose()
    {
        _image?.Dispose();
        _imagePaint.Dispose();
        _shadowPaint.Dispose();
        _textPaint.Dispose();
        _font.Dispose();
        _typeface.Dispose();
    }
}
