// CaptureWindow 分部类：预览重建、选中框与手绘（Sketch/Rough）路径。
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
using SkiaSharp;

namespace Sparrow.App.Views;

public partial class CaptureWindow
{
    private void AddNoteSelectionOutline(Rect bounds, Point center, double rotation = 0)
    {
        var outline = new Rectangle
        {
            Width = bounds.Width + 6,
            Height = bounds.Height + 6,
            Stroke = new SolidColorBrush(Color.Parse("#FFE5A44C")),
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
            IsHitTestVisible = false
        };

        if (Math.Abs(rotation) > 0.01)
        {
            var left = bounds.X - 3;
            var top = bounds.Y - 3;
            var width = bounds.Width + 6;
            var height = bounds.Height + 6;

            outline.RenderTransformOrigin = new RelativePoint(
                (center.X - left) / Math.Max(1, width),
                (center.Y - top) / Math.Max(1, height),
                RelativeUnit.Relative);
            outline.RenderTransform = new RotateTransform(rotation);
        }

        Canvas.SetLeft(outline, bounds.X - 3);
        Canvas.SetTop(outline, bounds.Y - 3);
        NoteLayer.Children.Add(outline);
    }

    private void AddPreviewShape(IAnnotation item)
    {
        var start = NoteLayer.Children.Count;

        // 手绘 / 涂鸦：预览层也用同一套抖动几何，画的时候就能看到（之前只有导出才手绘）
        if (TryAddSketchPreview(item))
        {
            ApplyPreviewRotation(item, start);
            return;
        }

        switch (item)
        {
            case ShapeAnnotation { Diamond: true } shape:
                var diamond = new Polygon
                {
                    Points = AnnotationStyle.DiamondPoints(shape.Rect).ToList(),
                    Stroke = new SolidColorBrush(Color.FromUInt32(shape.Color), shape.Opacity),
                    StrokeThickness = shape.Width,
                    Fill = PreviewShapeFill(shape)
                };
                ApplyDash(diamond, shape.Dash);
                NoteLayer.Children.Add(diamond);
                break;

            case ShapeAnnotation { Ellipse: true } shape:
                var ellipse = new Ellipse
                {
                    Width = shape.Rect.Width,
                    Height = shape.Rect.Height,
                    Stroke = new SolidColorBrush(Color.FromUInt32(shape.Color), shape.Opacity),
                    StrokeThickness = shape.Width,
                    Fill = PreviewShapeFill(shape)
                };
                ApplyDash(ellipse, shape.Dash);
                Canvas.SetLeft(ellipse, shape.Rect.X);
                Canvas.SetTop(ellipse, shape.Rect.Y);
                NoteLayer.Children.Add(ellipse);
                break;

            case ShapeAnnotation shape:
                var rectangle = new Rectangle
                {
                    Width = shape.Rect.Width,
                    Height = shape.Rect.Height,
                    RadiusX = shape.Radius,
                    RadiusY = shape.Radius,
                    Stroke = new SolidColorBrush(Color.FromUInt32(shape.Color), shape.Opacity),
                    StrokeThickness = shape.Width,
                    Fill = PreviewShapeFill(shape)
                };
                ApplyDash(rectangle, shape.Dash);
                Canvas.SetLeft(rectangle, shape.Rect.X);
                Canvas.SetTop(rectangle, shape.Rect.Y);
                NoteLayer.Children.Add(rectangle);
                break;

            case ArrowAnnotation arrow:
                var arrowBrush = new SolidColorBrush(Color.FromUInt32(arrow.Color), arrow.Opacity);
                AddPreviewLine(arrowBrush, arrow.From, arrow.To, arrow.Width, arrow.Dash);
                var (left, right) = ArrowGeometry.Head(
                    arrow.From, arrow.To, Math.Max(8, arrow.Width * 3.5));
                AddPreviewLine(arrowBrush, arrow.To, left, arrow.Width);
                AddPreviewLine(arrowBrush, arrow.To, right, arrow.Width);
                break;

            case LineAnnotation line:
                AddPreviewLine(
                    new SolidColorBrush(Color.FromUInt32(line.Color), line.Opacity),
                    line.From, line.To, line.Width, line.Dash);
                break;

            case DistanceAnnotation distance:
                AddDistancePreview(distance);
                break;

            case AngleAnnotation angle:
                AddAnglePreview(angle);
                break;

            case BracketAnnotation bracket:
                AddBracketPreview(bracket);
                break;

            case SpeechBubbleAnnotation bubble:
                AddBubblePreview(bubble);
                break;

            case SpotlightAnnotation spotlight:
            {
                // 整层压暗，只在区域处挖洞（和 DimLayer 同一个写法）
                var outer = new RectangleGeometry(
                    new Rect(0, 0, Math.Max(_selection.Width, 1), Math.Max(_selection.Height, 1)));
                Geometry hole = spotlight.Ellipse
                    ? new EllipseGeometry(spotlight.Rect)
                    : new RectangleGeometry(spotlight.Rect);

                NoteLayer.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole),
                    Fill = new SolidColorBrush(Color.FromUInt32(spotlight.Color), spotlight.Opacity),
                    IsHitTestVisible = false
                });
                break;
            }

            case MagnifierAnnotation magnifier:
            {
                var zoom = Math.Clamp(magnifier.Zoom, 1.2, 6);
                var radius = magnifier.Circle ? magnifier.Rect.Width / 2 : 2;
                var color = Color.FromUInt32(magnifier.BorderColor);

                // 预览只画镜片边框 + 倍数：放大画面在导出时按 source 采样，
                // 预览层拿不到「导出时的源图」，硬做容易和导出一致性出问题。
                var lens = new Border
                {
                    Width = magnifier.Rect.Width,
                    Height = magnifier.Rect.Height,
                    CornerRadius = new CornerRadius(radius),
                    BorderThickness = new Thickness(2),
                    BorderBrush = new SolidColorBrush(color),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(lens, magnifier.Rect.X);
                Canvas.SetTop(lens, magnifier.Rect.Y);
                NoteLayer.Children.Add(lens);

                var zoomLabel = new TextBlock
                {
                    Text = $"{zoom:0.#}×",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(color),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(zoomLabel, magnifier.Rect.X + 5);
                Canvas.SetTop(zoomLabel, magnifier.Rect.Y + 3);
                NoteLayer.Children.Add(zoomLabel);
                break;
            }

            case WatermarkAnnotation watermark:
                AddWatermarkPreview(watermark);
                break;

            case PenAnnotation { Points.Count: >= 2 } pen:
                var polyline = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromUInt32(pen.Color), pen.Opacity),
                    StrokeThickness = pen.Width,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Points = new List<Point>(pen.Points)
                };
                ApplyDash(polyline, pen.Dash);
                NoteLayer.Children.Add(polyline);
                break;

            case MosaicAnnotation mosaic:
                var cover = new Rectangle
                {
                    Width = mosaic.Rect.Width,
                    Height = mosaic.Rect.Height,
                    Fill = new SolidColorBrush(Color.Parse("#B0808080")),
                    Stroke = new SolidColorBrush(NoteColor, 0.6),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(cover, mosaic.Rect.X);
                Canvas.SetTop(cover, mosaic.Rect.Y);
                NoteLayer.Children.Add(cover);
                break;

            case TextAnnotation text:
                var label = new TextBlock
                {
                    Text = text.Text,
                    FontSize = text.FontSize,
                    FontWeight = text.Bold ? FontWeight.Bold : FontWeight.Normal,
                    FontStyle = text.Italic ? FontStyle.Italic : FontStyle.Normal,
                    Foreground = new SolidColorBrush(Color.FromUInt32(text.Color))
                };

                if (!string.IsNullOrWhiteSpace(text.FontFamily))
                    label.FontFamily = new FontFamily(text.FontFamily);

                // 对齐：按估计宽度定框再用 TextAlignment，锚点和导出端一致
                if (text.Align != TextAlign.Left)
                {
                    label.Width = EstimateTextWidth(text.Text, text.FontSize);
                    label.TextAlignment = text.Align switch
                    {
                        TextAlign.Center => TextAlignment.Center,
                        TextAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left
                    };
                }

                Canvas.SetLeft(label, text.Origin.X);
                Canvas.SetTop(label, text.Origin.Y - text.FontSize);

                if (text.BackgroundColor != 0)
                {
                    var background = new Border
                    {
                        Background = new SolidColorBrush(Color.FromUInt32(text.BackgroundColor)),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(AnnotationText.PaddingX, AnnotationText.PaddingY),
                        Child = label
                    };

                    Canvas.SetLeft(background, text.Origin.X - AnnotationText.PaddingX);
                    Canvas.SetTop(background, text.Origin.Y - text.FontSize - AnnotationText.PaddingY);
                    NoteLayer.Children.Add(background);
                    break;
                }

                NoteLayer.Children.Add(label);
                break;

            case NumberAnnotation number:
                var badgeLabel = NumberText.Format(number.Number, number.Format, number.Label);
                var hasBadgeLabel = !string.IsNullOrWhiteSpace(number.Label);
                var badgeFontSize = number.Size * 0.55;
                var badgeEstimate = EstimateTextWidth(badgeLabel, badgeFontSize);

                // 只有自定义标签才按文字撑宽，自动数字保持正圆/正方
                var badgeBounds = NumberGeometry.BadgeBounds(
                    number.Center, number.Size, number.Shape,
                    hasBadgeLabel ? badgeEstimate : 0);

                // 文字按徽章短边收敛，和导出端同一条规则
                var badgeLimit = Math.Min(badgeBounds.Width, badgeBounds.Height) * 0.8;
                if (badgeEstimate > badgeLimit && badgeEstimate > 0)
                {
                    badgeFontSize *= badgeLimit / badgeEstimate;

                    if (hasBadgeLabel)
                    {
                        badgeEstimate = EstimateTextWidth(badgeLabel, badgeFontSize);
                        badgeBounds = NumberGeometry.BadgeBounds(
                            number.Center, number.Size, number.Shape, badgeEstimate);
                    }
                }

                AddNumberDetailPreview(number);
                AddNumberBadgePreview(number, badgeBounds);

                var badgeAnchor = NumberGeometry.TextAnchor(badgeBounds, number.Shape);
                var badge = new TextBlock
                {
                    Text = badgeLabel,
                    FontSize = badgeFontSize,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(
                        number.Shape == NumberShape.OutlineCircle
                            ? Color.FromUInt32(number.Color)
                            : Colors.White),
                    Width = badgeBounds.Width,
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(badge, badgeAnchor.X - badgeBounds.Width / 2);
                Canvas.SetTop(badge, badgeAnchor.Y - badgeFontSize * 0.6);
                NoteLayer.Children.Add(badge);
                break;
        }

        ApplyPreviewRotation(item, start);
    }

    /// <summary>整条标注按中心旋转：把本次新增的子元素装进一个旋转 Canvas。</summary>
    private void ApplyPreviewRotation(IAnnotation item, int start)
    {
        var rotation = AnnotationGeometry.RotationOf(item);
        if (Math.Abs(rotation) > 0.01 && NoteLayer.Children.Count > start)
        {
            var added = new List<Control>();
            for (var index = NoteLayer.Children.Count - 1; index >= start; index--)
            {
                added.Insert(0, NoteLayer.Children[index]);
                NoteLayer.Children.RemoveAt(index);
            }

            var center = NoteRotationCenter(item);
            var container = new Canvas
            {
                Width = _selection.Width,
                Height = _selection.Height,
                RenderTransformOrigin = new RelativePoint(
                    center.X / Math.Max(1, _selection.Width),
                    center.Y / Math.Max(1, _selection.Height),
                    RelativeUnit.Relative),
                RenderTransform = new RotateTransform(rotation)
            };

            foreach (var child in added)
                container.Children.Add(child);

            NoteLayer.Children.Add(container);
        }
    }

    /// <summary>预览用的描边规格：轮廓顶点 + 是否闭合 + 短边 + 线宽颜色。</summary>
    private readonly record struct SketchSpec(
        LineStyle Style,
        IReadOnlyList<Point[]>? Contours,
        bool Closed,
        double ShortSide,
        double Width,
        uint Color,
        double Opacity);

    /// <summary>
    /// 手绘 / 涂鸦的实时预览：用和导出相同的抖动算法生成路径，
    /// 这样画的时候就能看到效果，而不是只有导出的 PNG 才是手绘。
    /// </summary>
    private bool TryAddSketchPreview(IAnnotation item)
    {
        var spec = SketchOf(item);

        if (spec.Style == LineStyle.Neat || spec.Contours is null)
            return false;

        var geometry = BuildSketchGeometry(spec.Contours, spec.Closed, spec.Style, spec.ShortSide);

        // 形状先铺填充，再描手绘边
        if (item is ShapeAnnotation shape && shape.Fill is ShapeFill.Soft or ShapeFill.Solid)
        {
            NoteLayer.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(
                    Color.FromUInt32(AnnotationStyle.FillColor(shape.Color, shape.FillColor)),
                    AnnotationStyle.FillAlpha(shape.Fill, shape.Opacity)),
                IsHitTestVisible = false
            });
        }

        NoteLayer.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(Color.FromUInt32(spec.Color), spec.Opacity),
            StrokeThickness = Math.Max(1, spec.Width),
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        });

        // 箭头头部保持实心三角，和导出一致
        if (item is ArrowAnnotation arrow)
        {
            var (left, right) = ArrowGeometry.Head(
                arrow.From, arrow.To, Math.Max(8, arrow.Width * 3.5));

            NoteLayer.Children.Add(new Polygon
            {
                Points = new List<Point> { arrow.To, left, right },
                Fill = new SolidColorBrush(Color.FromUInt32(arrow.Color), arrow.Opacity),
                IsHitTestVisible = false
            });
        }

        return true;
    }

    private static SketchSpec SketchOf(IAnnotation item) => item switch
    {
        ShapeAnnotation shape => new SketchSpec(
            shape.Style, [ShapeContours(shape)], true,
            Math.Min(shape.Rect.Width, shape.Rect.Height),
            shape.Width, shape.Color, shape.Opacity),

        ArrowAnnotation arrow => new SketchSpec(
            arrow.Style, [new[] { arrow.From, arrow.To }], false,
            Distance(arrow.From, arrow.To), arrow.Width, arrow.Color, arrow.Opacity),

        LineAnnotation line => new SketchSpec(
            line.Style, [new[] { line.From, line.To }], false,
            Distance(line.From, line.To), line.Width, line.Color, line.Opacity),

        DistanceAnnotation distance => new SketchSpec(
            distance.Style, [new[] { distance.From, distance.To }], false,
            Distance(distance.From, distance.To),
            distance.Width, distance.Color, distance.Opacity),

        BracketAnnotation bracket => new SketchSpec(
            bracket.Style, [AnnotationGeometry.BracketPoints(bracket)], false,
            Math.Min(bracket.Rect.Width, bracket.Rect.Height),
            bracket.Width, bracket.Color, bracket.Opacity),

        PenAnnotation pen => new SketchSpec(
            pen.Style, [pen.Points.ToArray()], false, PenShortSide(pen.Points),
            pen.Width, pen.Color, pen.Opacity),

        _ => new SketchSpec(LineStyle.Neat, null, false, 0, 0, 0, 1)
    };

    /// <summary>形状轮廓顶点：和导出用的 BuildShapeOutline 一一对应。</summary>
    private static Point[] ShapeContours(ShapeAnnotation shape)
    {
        var rect = shape.Rect;

        if (shape.Diamond)
        {
            return
            [
                new Point(rect.Center.X, rect.Top),
                new Point(rect.Right, rect.Center.Y),
                new Point(rect.Center.X, rect.Bottom),
                new Point(rect.Left, rect.Center.Y)
            ];
        }

        if (shape.Ellipse)
            return EllipsePoints(rect, 40);

        var radius = Math.Clamp(shape.Radius, 0, Math.Min(rect.Width, rect.Height) / 2);

        if (radius > 0)
            return RoundedRectPoints(rect, radius, 6);

        return
        [
            new Point(rect.Left, rect.Top),
            new Point(rect.Right, rect.Top),
            new Point(rect.Right, rect.Bottom),
            new Point(rect.Left, rect.Bottom)
        ];
    }

    private static Point[] EllipsePoints(Rect rect, int count)
    {
        var points = new Point[count];

        for (var index = 0; index < count; index++)
        {
            var angle = Math.PI * 2 * index / count;
            points[index] = new Point(
                rect.Center.X + rect.Width / 2 * Math.Cos(angle),
                rect.Center.Y + rect.Height / 2 * Math.Sin(angle));
        }

        return points;
    }

    private static Point[] RoundedRectPoints(Rect rect, double radius, int arcSegments)
    {
        var points = new List<Point>();
        var right = rect.Right - radius;
        var bottom = rect.Bottom - radius;
        var left = rect.Left + radius;
        var top = rect.Top + radius;

        points.Add(new Point(left, rect.Top));
        points.Add(new Point(right, rect.Top));
        AddArc(points, right, top, radius, -Math.PI / 2, 0, arcSegments);
        points.Add(new Point(rect.Right, bottom));
        AddArc(points, right, bottom, radius, 0, Math.PI / 2, arcSegments);
        points.Add(new Point(left, rect.Bottom));
        AddArc(points, left, bottom, radius, Math.PI / 2, Math.PI, arcSegments);
        points.Add(new Point(rect.Left, top));
        AddArc(points, left, top, radius, Math.PI, Math.PI * 1.5, arcSegments);

        return points.ToArray();
    }

    private static void AddArc(List<Point> points, double centerX, double centerY, double radius,
        double from, double to, int segments)
    {
        for (var index = 1; index <= segments; index++)
        {
            var angle = from + (to - from) * index / segments;
            points.Add(new Point(
                centerX + radius * Math.Cos(angle),
                centerY + radius * Math.Sin(angle)));
        }
    }

    private static double PenShortSide(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
            return 0;

        var minX = points[0].X;
        var maxX = minX;
        var minY = points[0].Y;
        var maxY = minY;

        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        return Math.Min(maxX - minX, maxY - minY);
    }

    /// <summary>和导出端同一套抖动：每条原始边一段三次贝塞尔，涂鸦画两遍。</summary>
    private static StreamGeometry BuildSketchGeometry(
        IReadOnlyList<Point[]> contours, bool closed, LineStyle style, double shortSide)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();

        var amplitude = (float)Math.Min(
            style == LineStyle.Rough ? 4 : 2, Math.Max(0.5, shortSide * 0.1));
        var seed = style == LineStyle.Rough ? 0x5EED1234u : 0xA17C0DEu;
        var passes = style == LineStyle.Rough ? 2 : 1;

        for (var pass = 0; pass < passes; pass++)
        {
            var passAmplitude = amplitude * (pass == 0 ? 1f : 0.5f);
            var segment = 0;

            foreach (var contour in contours)
            {
                if (contour.Length < 2)
                    continue;

                context.BeginFigure(contour[0], false);

                var count = contour.Length;
                var segments = closed ? count : count - 1;

                for (var index = 0; index < segments; index++)
                {
                    var start = contour[index];
                    var end = contour[(index + 1) % count];
                    var (c1, c2) = SketchControls(
                        start, end, seed, passAmplitude, pass == 0, segment++);

                    context.CubicBezierTo(c1, c2, end);
                }

                context.EndFigure(closed);
            }
        }

        return geometry;
    }

    private static (Point C1, Point C2) SketchControls(
        Point start, Point end, uint seed, float amplitude, bool firstPass, int segment)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var span = Math.Sqrt(dx * dx + dy * dy);

        if (span <= 0.0001)
            return (start, start);

        var nx = -dy / span;
        var ny = dx / span;

        var bendSample = AnnotationRenderer.Sample(seed ^ 0x632be59Bu, segment * 8 + 5);
        var bend = Math.Sign(bendSample) * (0.88 + 0.04 * Math.Abs(bendSample)) * (firstPass ? 1 : -1);

        var jitter1 = amplitude * (bend + 0.06 * AnnotationRenderer.Sample(seed, segment * 8 + 1));
        var jitter2 = amplitude * (bend + 0.06 * AnnotationRenderer.Sample(seed, segment * 8 + 2));

        return (
            new Point(start.X + dx * 0.28 + nx * jitter1, start.Y + dy * 0.28 + ny * jitter1),
            new Point(start.X + dx * 0.71 + nx * jitter2, start.Y + dy * 0.71 + ny * jitter2));
    }

    // ---------- 文字输入 ----------
}
