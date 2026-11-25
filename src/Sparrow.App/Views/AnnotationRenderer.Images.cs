// AnnotationRenderer 分部类：放大镜、聚光灯与马赛克。
using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

internal static partial class AnnotationRenderer
{
    /// <summary>放大镜：在原矩形里显示「中心 1/Zoom 区域」的放大结果（镜片不动，画面放大）。</summary>
    private static void DrawMagnifier(SKCanvas canvas, MagnifierAnnotation magnifier, SKPaint paint,
        SKBitmap source)
    {
        var rect = magnifier.Rect;
        if (rect.Width < 4 || rect.Height < 4)
            return;

        var zoom = Math.Clamp(magnifier.Zoom, 1.2, 6);
        var dest = new SKRect(
            (float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);

        // 源区域 = 目标区域按 1/zoom 从中心收缩
        var sourceWidth = rect.Width / zoom;
        var sourceHeight = rect.Height / zoom;
        var sourceRect = new Rect(
            rect.X + rect.Width / 2 - sourceWidth / 2,
            rect.Y + rect.Height / 2 - sourceHeight / 2,
            sourceWidth, sourceHeight);

        var matrix = canvas.TotalMatrix;
        var topLeft = matrix.MapPoint(new SKPoint((float)sourceRect.X, (float)sourceRect.Y));
        var bottomRight = matrix.MapPoint(new SKPoint(
            (float)sourceRect.Right, (float)sourceRect.Bottom));
        var src = new SKRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);

        src = new SKRect(
            Math.Max(0, src.Left),
            Math.Max(0, src.Top),
            Math.Min(source.Width, src.Right),
            Math.Min(source.Height, src.Bottom));

        if (src.Width < 1 || src.Height < 1)
            return;

        canvas.Save();

        if (magnifier.Circle)
        {
            using var clip = new SKPath();
            clip.AddOval(dest);
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            canvas.ClipRect(dest);
        }

        using (var image = SKImage.FromBitmap(source))
        {
            canvas.DrawImage(
                image, src, dest,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        canvas.Restore();

        // 边框：圆形描边椭圆，方形描边矩形
        paint.Color = new SKColor(magnifier.BorderColor);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 2;
        paint.PathEffect = null;

        if (magnifier.Circle)
            canvas.DrawOval(dest, paint);
        else
            canvas.DrawRect(dest, paint);
    }

    /// <summary>聚光灯：用一个巨大的外框 + 内洞组成 EvenOdd 路径，一次填充完成压暗。</summary>
    private static void DrawSpotlight(SKCanvas canvas, SpotlightAnnotation spotlight)
    {
        var rect = new SKRect(
            (float)spotlight.Rect.X,
            (float)spotlight.Rect.Y,
            (float)spotlight.Rect.Right,
            (float)spotlight.Rect.Bottom);

        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.AddRect(new SKRect(-10000, -10000, 10000, 10000));
        if (spotlight.Ellipse)
            path.AddOval(rect);
        else
            path.AddRect(rect);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = WithOpacity(spotlight.Color, spotlight.Opacity)
        };

        canvas.DrawPath(path, paint);
    }

    /// <summary>马赛克：像素化用「缩小再最近邻放大」，模糊用 Skia 的模糊滤镜。</summary>
    private static void DrawMosaic(SKCanvas canvas, MosaicAnnotation mosaic, SKBitmap source)
    {
        var rect = new SKRect(
            (float)mosaic.Rect.X,
            (float)mosaic.Rect.Y,
            (float)mosaic.Rect.Right,
            (float)mosaic.Rect.Bottom);

        // 目标区域换算到源图坐标（画布此时已按 scale 缩放）
        var matrix = canvas.TotalMatrix;
        var sourceLeft = matrix.MapPoint(new SKPoint(rect.Left, rect.Top));
        var sourceRight = matrix.MapPoint(new SKPoint(rect.Right, rect.Bottom));
        var sourceRect = new SKRect(
            Math.Max(0, sourceLeft.X),
            Math.Max(0, sourceLeft.Y),
            Math.Min(source.Width, sourceRight.X),
            Math.Min(source.Height, sourceRight.Y));

        if (sourceRect.Width < 1 || sourceRect.Height < 1)
            return;

        canvas.Save();
        canvas.ClipRect(rect);

        if (mosaic.Blur)
        {
            using var filter = SKImageFilter.CreateBlur(8, 8);
            using var paint = new SKPaint { ImageFilter = filter };
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, paint);
        }
        else
        {
            var cell = Math.Max(2, mosaic.Cell);
            var columns = Math.Max(1, (int)(sourceRect.Width / cell));
            var rows = Math.Max(1, (int)(sourceRect.Height / cell));

            using var sourceImage = SKImage.FromBitmap(source);
            using var small = new SKBitmap(columns, rows);

            // 线性采样：一格之内的像素要被平均，否则棋盘格缩小后还是黑白
            using (var smallCanvas = new SKCanvas(small))
            {
                smallCanvas.DrawImage(
                    sourceImage,
                    sourceRect,
                    new SKRect(0, 0, columns, rows),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }

            using var image = SKImage.FromBitmap(small);
            canvas.DrawImage(
                image,
                new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        canvas.Restore();
    }

    /// <summary>
    /// 水印：裁剪到区域，绕区域中心旋转后按网格平铺文字。
    /// 平铺范围取对角线正方形，任意角度都能盖满；数量上限由 WatermarkLayout 控制。
    /// </summary>
    private static void DrawWatermark(SKCanvas canvas, WatermarkAnnotation watermark)
    {
        if (string.IsNullOrWhiteSpace(watermark.Text) ||
            watermark.Area.Width < 4 || watermark.Area.Height < 4)
            return;

        var area = new SKRect(
            (float)watermark.Area.X,
            (float)watermark.Area.Y,
            (float)watermark.Area.Right,
            (float)watermark.Area.Bottom);

        var spacing = WatermarkLayout.FitSpacing(watermark.Area, watermark.Spacing);

        using var font = AnnotationText.CreateFont(watermark.FontSize, bold: true);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = WithOpacity(watermark.Color, watermark.Opacity)
        };

        canvas.Save();
        canvas.ClipRect(area);
        canvas.RotateDegrees(
            (float)watermark.Angle,
            (area.Left + area.Right) / 2,
            (area.Top + area.Bottom) / 2);

        // 0.35em 的基线偏移约等于把文字垂直居中到格点
        var baseline = (float)(watermark.FontSize * 0.35);
        foreach (var point in WatermarkLayout.Tiles(
                     watermark.Area, spacing, WatermarkLayout.TileCap))
        {
            canvas.DrawText(
                watermark.Text,
                (float)point.X,
                (float)point.Y + baseline,
                SKTextAlign.Center,
                font,
                paint);
        }

        canvas.Restore();
    }
}
