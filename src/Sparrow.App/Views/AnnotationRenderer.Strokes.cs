// AnnotationRenderer 分部类：虚线效果与手绘（Sketch/Rough）描边路径。
using Avalonia;
using SkiaSharp;

namespace Sparrow.App.Views;

internal static partial class AnnotationRenderer
{
    /// <summary>虚线 / 点线：dash 长度跟着线宽走，粗细变化时观感一致。</summary>
    private static SKPathEffect? DashEffect(LineDash dash, double width)
    {
        var w = (float)Math.Max(1, width);

        return dash switch
        {
            LineDash.Dash => SKPathEffect.CreateDash([w * 3, w * 2], 0),
            LineDash.Dot => SKPathEffect.CreateDash([w * 0.1f, w * 1.8f], 0),
            _ => null
        };
    }

    /// <summary>描边几何：轮廓顶点 + 是否闭合 + 短边长度（手绘振幅按它算）。</summary>
    private readonly record struct StrokeGeometry(
        IReadOnlyList<SKPoint[]> Contours, bool Closed, float ShortEdge);

    /// <summary>按线型描边：图案决定实线/虚线/点线，笔触决定规整/手绘/涂鸦。</summary>
    private static void StrokePath(SKCanvas canvas, StrokeGeometry geometry, SKPaint paint,
        LineDash dash, LineStyle style, double width)
    {
        paint.PathEffect = null;

        if (style != LineStyle.Neat)
        {
            StrokeSketch(canvas, geometry, paint, dash, style, width);
            return;
        }

        paint.PathEffect = DashEffect(dash, width);
        canvas.DrawPath(BuildStrokePath(geometry), paint);
        paint.PathEffect = null;
    }

    private static SKPath BuildStrokePath(StrokeGeometry geometry)
    {
        var path = new SKPath();

        foreach (var contour in geometry.Contours)
        {
            if (contour.Length == 0)
                continue;

            path.MoveTo(contour[0]);
            for (var index = 1; index < contour.Length; index++)
                path.LineTo(contour[index]);

            if (geometry.Closed)
                path.Close();
        }

        return path;
    }

    /// <summary>
    /// 手绘笔触：把每条原始边当成一段三次贝塞尔，端点不动、控制点沿法线鼓出，
    /// 得到「像人手画上去」的歪线；涂鸦再反向画一遍、幅度减半，形成双线（仅实线）。
    /// </summary>
    private static void StrokeSketch(SKCanvas canvas, StrokeGeometry geometry, SKPaint paint,
        LineDash dash, LineStyle style, double width)
    {
        var amplitude = SketchAmplitude(style, geometry.ShortEdge);
        var passes = style == LineStyle.Rough && dash == LineDash.Solid ? 2 : 1;

        for (var pass = 0; pass < passes; pass++)
        {
            using var varied = BuildSketchPath(geometry, style, pass, amplitude);
            paint.PathEffect = DashEffect(dash, width);
            canvas.DrawPath(varied, paint);
            paint.PathEffect = null;
        }
    }

    /// <summary>
    /// 手绘振幅：上限手绘 2px / 涂鸦 4px，再按图形短边的 10% 收缩，
    /// 避免小图形被抖成一团。
    /// </summary>
    private static float SketchAmplitude(LineStyle style, float shortSide)
    {
        var limit = style == LineStyle.Rough ? 4f : 2f;
        return MathF.Min(limit, MathF.Max(0.5f, shortSide * 0.1f));
    }

    private static SKPath BuildSketchPath(StrokeGeometry geometry, LineStyle style, int pass, float amplitude)
    {
        var options = new SketchOptions(
            Seed: style == LineStyle.Rough ? 0x5EED1234u : 0xA17C0DEu,
            Amplitude: amplitude * (pass == 0 ? 1f : 0.5f),
            FirstPass: pass == 0);

        var result = new SKPath();
        var index = 0;

        foreach (var contour in geometry.Contours)
        {
            if (contour.Length < 2)
                continue;

            result.MoveTo(contour[0]);

            var count = contour.Length;
            var segments = geometry.Closed ? count : count - 1;

            for (var segment = 0; segment < segments; segment++)
            {
                var start = contour[segment];
                var end = contour[(segment + 1) % count];
                var (c1, c2) = SketchControls(start, end, options, index++);
                result.CubicTo(c1, c2, end);
            }

            if (geometry.Closed)
                result.Close();
        }

        return result;
    }

    private readonly record struct SketchOptions(uint Seed, float Amplitude, bool FirstPass);

    /// <summary>一段弓形贝塞尔：端点不动，控制点沿法线鼓出，第二遍反向。</summary>
    private static (SKPoint C1, SKPoint C2) SketchControls(
        SKPoint start, SKPoint end, SketchOptions options, int segment)
    {
        var delta = new SKPoint(end.X - start.X, end.Y - start.Y);
        var span = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

        if (span <= 0.0001f)
            return (start, start);

        var nx = -delta.Y / span;
        var ny = delta.X / span;

        var bendSample = Sample(options.Seed ^ 0x632be59bu, segment * 8 + 5);
        var bend = MathF.Sign(bendSample) * (0.88f + 0.04f * MathF.Abs(bendSample)) *
                   (options.FirstPass ? 1f : -1f);

        var jitter1 = options.Amplitude * (bend + 0.06f * Sample(options.Seed, segment * 8 + 1));
        var jitter2 = options.Amplitude * (bend + 0.06f * Sample(options.Seed, segment * 8 + 2));

        return (
            new SKPoint(
                start.X + delta.X * 0.28f + nx * jitter1,
                start.Y + delta.Y * 0.28f + ny * jitter1),
            new SKPoint(
                start.X + delta.X * 0.71f + nx * jitter2,
                start.Y + delta.Y * 0.71f + ny * jitter2));
    }

    /// <summary>形状的轮廓顶点：矩形/菱形 4 点，椭圆/圆角矩形按弧采样。</summary>
    private static StrokeGeometry ShapeStrokeGeometry(SKRect rect, float radius, bool ellipse, bool diamond)
    {
        var shortSide = MathF.Min(rect.Width, rect.Height);

        if (diamond)
        {
            return new StrokeGeometry(
                [
                    new[]
                    {
                        new SKPoint(rect.MidX, rect.Top), new SKPoint(rect.Right, rect.MidY),
                        new SKPoint(rect.MidX, rect.Bottom), new SKPoint(rect.Left, rect.MidY)
                    }
                ],
                true,
                shortSide);
        }

        if (ellipse)
            return new StrokeGeometry([SampleEllipse(rect, 48)], true, shortSide);

        if (radius > 0)
            return new StrokeGeometry([SampleRoundedRect(rect, radius, 8)], true, shortSide);

        return new StrokeGeometry(
            [
                new[]
                {
                    new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top),
                    new SKPoint(rect.Right, rect.Bottom), new SKPoint(rect.Left, rect.Bottom)
                }
            ],
            true,
            shortSide);
    }

    private static SKPoint[] SampleEllipse(SKRect rect, int count)
    {
        var points = new SKPoint[count];
        var centerX = rect.MidX;
        var centerY = rect.MidY;
        var radiusX = rect.Width / 2;
        var radiusY = rect.Height / 2;

        for (var index = 0; index < count; index++)
        {
            var angle = MathF.PI * 2 * index / count;
            points[index] = new SKPoint(
                centerX + radiusX * MathF.Cos(angle),
                centerY + radiusY * MathF.Sin(angle));
        }

        return points;
    }

    private static SKPoint[] SampleRoundedRect(SKRect rect, float radius, int arcSegments)
    {
        var points = new List<SKPoint>();
        var right = rect.Right - radius;
        var bottom = rect.Bottom - radius;
        var left = rect.Left + radius;
        var top = rect.Top + radius;

        // 上边 → 右上角 → 右边 → 右下角 → 下边 → 左下角 → 左边 → 左上角
        points.Add(new SKPoint(left, rect.Top));
        points.Add(new SKPoint(right, rect.Top));
        AddArc(points, right, top, radius, -MathF.PI / 2, 0, arcSegments);
        points.Add(new SKPoint(rect.Right, bottom));
        AddArc(points, right, bottom, radius, 0, MathF.PI / 2, arcSegments);
        points.Add(new SKPoint(left, rect.Bottom));
        AddArc(points, left, bottom, radius, MathF.PI / 2, MathF.PI, arcSegments);
        points.Add(new SKPoint(rect.Left, top));
        AddArc(points, left, top, radius, MathF.PI, MathF.PI * 1.5f, arcSegments);

        return points.ToArray();
    }

    private static void AddArc(List<SKPoint> points, float centerX, float centerY, float radius,
        float from, float to, int segments)
    {
        for (var index = 1; index <= segments; index++)
        {
            var angle = from + (to - from) * index / segments;
            points.Add(new SKPoint(
                centerX + radius * MathF.Cos(angle),
                centerY + radius * MathF.Sin(angle)));
        }
    }

    /// <summary>一条直线段的描边几何。</summary>
    private static StrokeGeometry SegmentStrokeGeometry(SKPoint from, SKPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        return new StrokeGeometry([new[] { from, to }], false, length);
    }

    /// <summary>折线的描边几何：振幅按包围盒短边算。</summary>
    private static StrokeGeometry PolylineStrokeGeometry(SKPoint[] points) =>
        new StrokeGeometry([points], false, ShortSide(points));

    private static StrokeGeometry PenStrokeGeometry(IReadOnlyList<Point> points)
    {
        var vertices = new SKPoint[points.Count];
        for (var index = 0; index < points.Count; index++)
            vertices[index] = new SKPoint((float)points[index].X, (float)points[index].Y);

        return new StrokeGeometry([vertices], false, ShortSide(vertices));
    }

    /// <summary>顶点集包围盒的短边；手绘振幅按它算，避免小图形被抖变形。</summary>
    private static float ShortSide(SKPoint[] points)
    {
        if (points.Length == 0)
            return 0;

        var minX = points[0].X;
        var maxX = points[0].X;
        var minY = points[0].Y;
        var maxY = points[0].Y;

        for (var index = 1; index < points.Length; index++)
        {
            minX = MathF.Min(minX, points[index].X);
            maxX = MathF.Max(maxX, points[index].X);
            minY = MathF.Min(minY, points[index].Y);
            maxY = MathF.Max(maxY, points[index].Y);
        }

        return MathF.Min(maxX - minX, maxY - minY);
    }

    /// <summary>确定性随机：同一个 seed/index 永远得到同一个值，预览和导出保持一致。</summary>
    internal static float Sample(uint seed, int index)
    {
        var value = seed + 0x9e3779b9u * (uint)(index + 1);
        value = (value ^ (value >> 16)) * 0x85ebca6bu;
        value = (value ^ (value >> 13)) * 0xc2b2ae35u;
        value ^= value >> 16;
        return value / 2147483647.5f - 1f;
    }
}
