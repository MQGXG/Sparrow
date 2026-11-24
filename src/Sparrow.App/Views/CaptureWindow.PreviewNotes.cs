// CaptureWindow 分部类：画布文本编辑框与水印/测距/角度/括号/序号/气泡等图元预览。
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
    private void ShowTextEditor(Point origin)
    {
        _textEditTarget = TextEditTarget.NewText;
        _textEditNoteIndex = -1;
        _textOrigin = origin;
        TextNoteBox.Text = string.Empty;
        TextNoteBox.FontSize = _noteFontSize;
        TextNoteBox.FontWeight = _noteBold ? FontWeight.Bold : FontWeight.Normal;
        TextNoteBox.Foreground = new SolidColorBrush(_noteColor);
        TextNoteBox.CaretBrush = new SolidColorBrush(_noteColor);
        Canvas.SetLeft(TextNoteBox, _selection.X + origin.X);
        Canvas.SetTop(TextNoteBox, _selection.Y + origin.Y);
        TextNoteBox.IsVisible = true;
        TextNoteBox.Focus();
    }

    /// <summary>改写已有标注里的文字（水印，后续气泡）；返回 false 表示索引或类型不支持。</summary>
    internal bool EditNoteText(int index)
    {
        if (index < 0 || index >= _notes.Items.Count)
            return false;

        var (origin, text, fontSize, color, bold) = _notes.Items[index] switch
        {
            WatermarkAnnotation watermark => (
                new Point(
                    Math.Clamp(watermark.Area.X + 8, 0, Math.Max(0, _selection.Width - 120)),
                    Math.Clamp(watermark.Area.Y + 8, 0, Math.Max(0, _selection.Height - 40))),
                watermark.Text,
                watermark.FontSize,
                Color.FromUInt32(watermark.Color),
                true),
            SpeechBubbleAnnotation bubble => (
                new Point(
                    Math.Clamp(bubble.Rect.X + 8, 0, Math.Max(0, _selection.Width - 120)),
                    Math.Clamp(bubble.Rect.Y + 6, 0, Math.Max(0, _selection.Height - 40))),
                bubble.Text,
                bubble.FontSize,
                Color.FromUInt32(bubble.Color),
                false),
            NumberAnnotation { Combo: NumberCombo.Text } number => (
                new Point(
                    Math.Clamp(NumberGeometry.DetailBounds(number).X + 6, 0,
                        Math.Max(0, _selection.Width - 120)),
                    Math.Clamp(NumberGeometry.DetailBounds(number).Y + 3, 0,
                        Math.Max(0, _selection.Height - 40))),
                number.Text ?? string.Empty,
                number.TextSize,
                Colors.White,
                false),
            _ => (default(Point), string.Empty, 0d, default(Color), false)
        };

        if (fontSize <= 0)
            return false;

        _textEditTarget = TextEditTarget.Note;
        _textEditNoteIndex = index;
        _textOrigin = origin;
        TextNoteBox.Text = text;
        TextNoteBox.FontSize = fontSize;
        TextNoteBox.FontWeight = bold ? FontWeight.Bold : FontWeight.Normal;
        TextNoteBox.Foreground = new SolidColorBrush(color);
        TextNoteBox.CaretBrush = new SolidColorBrush(color);
        Canvas.SetLeft(TextNoteBox, _selection.X + origin.X);
        Canvas.SetTop(TextNoteBox, _selection.Y + origin.Y);
        TextNoteBox.IsVisible = true;
        TextNoteBox.CaretIndex = text.Length;
        TextNoteBox.Focus();
        return true;
    }

    private void HideTextEditor()
    {
        TextNoteBox.IsVisible = false;
        TextNoteBox.Text = string.Empty;
        _textEditTarget = TextEditTarget.NewText;
        _textEditNoteIndex = -1;
    }

    internal void CommitTextNote()
    {
        if (!TextNoteBox.IsVisible)
            return;

        var text = TextNoteBox.Text?.Trim() ?? string.Empty;
        var target = _textEditTarget;
        var index = _textEditNoteIndex;
        var origin = _textOrigin;
        HideTextEditor();

        if (target == TextEditTarget.Note)
        {
            if (index >= 0 && index < _notes.Items.Count)
            {
                IAnnotation? updated = _notes.Items[index] switch
                {
                    WatermarkAnnotation watermark when text.Length > 0 => watermark with { Text = text },
                    WatermarkAnnotation watermark => watermark,
                    SpeechBubbleAnnotation bubble => bubble with { Text = text },
                    NumberAnnotation number when number.Combo == NumberCombo.Text
                        => number with { Text = text.Length > 0 ? text : null },
                    _ => null
                };

                if (updated is not null)
                {
                    _notes.ReplaceWithHistory(index, updated);
                    MarkNoteMutation();

                    // 下次新建水印沿用刚改过的文字
                    if (updated is WatermarkAnnotation watermark)
                        _watermarkText = watermark.Text;
                }
            }

            RebuildNotePreview();
            UpdateToolButtons();
            return;
        }

        if (text.Length == 0)
            return;

        // 编辑器左上角对齐，Skia 的 DrawText 用基线：往下让一个字高
        _notes.Add(new TextAnnotation(
            new Point(origin.X, origin.Y + _noteFontSize),
            text,
            _noteColor.ToUInt32(),
            _noteFontSize,
            _noteBold,
            FontFamily: _noteFontFamily,
            Italic: _noteItalic,
            Align: _noteAlign,
            BackgroundColor: _textBackground,
            StrokeColor: _textStrokeColor,
            StrokeWidth: _textStrokeWidth));
        MarkNoteMutation();

        RebuildNotePreview();
        UpdateToolButtons();
    }

    private void OnTextNoteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTextNote();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideTextEditor();
            e.Handled = true;
        }
    }

    private void OnTextNoteLostFocus(object? sender, RoutedEventArgs e) => CommitTextNote();

    /// <summary>水印预览：父层按区域裁剪，子层整体旋转，保证裁剪框始终是轴向矩形。</summary>
    private void AddWatermarkPreview(WatermarkAnnotation watermark)
    {
        if (string.IsNullOrWhiteSpace(watermark.Text) ||
            watermark.Area.Width < 4 || watermark.Area.Height < 4)
            return;

        var spacing = WatermarkLayout.FitSpacing(watermark.Area, watermark.Spacing);
        var brush = new SolidColorBrush(Color.FromUInt32(watermark.Color), watermark.Opacity);

        var clip = new Border
        {
            Width = watermark.Area.Width,
            Height = watermark.Area.Height,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(clip, watermark.Area.X);
        Canvas.SetTop(clip, watermark.Area.Y);

        var tiles = new Canvas
        {
            Width = watermark.Area.Width,
            Height = watermark.Area.Height,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new RotateTransform(watermark.Angle)
        };

        foreach (var point in WatermarkLayout.Tiles(
                     watermark.Area, spacing, WatermarkLayout.PreviewCap))
        {
            var text = new TextBlock
            {
                Text = watermark.Text,
                FontSize = watermark.FontSize,
                FontWeight = FontWeight.Bold,
                Foreground = brush
            };
            text.Measure(Size.Infinity);
            Canvas.SetLeft(text, point.X - watermark.Area.X - text.DesiredSize.Width / 2);
            Canvas.SetTop(text, point.Y - watermark.Area.Y - text.DesiredSize.Height / 2);
            tiles.Children.Add(text);
        }

        clip.Child = tiles;
        NoteLayer.Children.Add(clip);
    }

    /// <summary>测距预览：线 + 两端刻度 + 中点上方标签，和导出同几何。</summary>
    private void AddDistancePreview(DistanceAnnotation distance)
    {
        var brush = new SolidColorBrush(Color.FromUInt32(distance.Color), distance.Opacity);
        AddPreviewLine(brush, distance.From, distance.To, distance.Width, distance.Dash);

        var dx = distance.To.X - distance.From.X;
        var dy = distance.To.Y - distance.From.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.01)
            return;

        var nx = -dy / length;
        var ny = dx / length;
        if (ny > 0)
        {
            nx = -nx;
            ny = -ny;
        }

        const double tick = 4;
        AddPreviewLine(
            brush,
            new Point(distance.From.X - nx * tick, distance.From.Y - ny * tick),
            new Point(distance.From.X + nx * tick, distance.From.Y + ny * tick),
            distance.Width);
        AddPreviewLine(
            brush,
            new Point(distance.To.X - nx * tick, distance.To.Y - ny * tick),
            new Point(distance.To.X + nx * tick, distance.To.Y + ny * tick),
            distance.Width);

        if (string.IsNullOrEmpty(distance.Label))
            return;

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B3000000")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = distance.Label,
                FontSize = 11,
                Foreground = Brushes.White
            }
        };

        pill.Measure(Size.Infinity);
        var centerX = (distance.From.X + distance.To.X) / 2 + nx * 12;
        var centerY = (distance.From.Y + distance.To.Y) / 2 + ny * 12;
        Canvas.SetLeft(pill, centerX - pill.DesiredSize.Width / 2);
        Canvas.SetTop(pill, centerY - pill.DesiredSize.Height / 2);
        NoteLayer.Children.Add(pill);
    }

    /// <summary>角度预览：两条射线 + 圆弧 + 弧外侧标签，圆弧半径随短边收缩。</summary>
    private void AddAnglePreview(AngleAnnotation angle)
    {
        var brush = new SolidColorBrush(Color.FromUInt32(angle.Color), angle.Opacity);
        AddPreviewLine(brush, angle.Vertex, angle.A, angle.Width);
        AddPreviewLine(brush, angle.Vertex, angle.B, angle.Width);

        var lengthA = Distance(angle.Vertex, angle.A);
        var lengthB = Distance(angle.Vertex, angle.B);
        var radius = Math.Min(36, Math.Min(lengthA, lengthB) * 0.7);
        if (radius < 6)
            return;

        var unitA = Direction(angle.Vertex, angle.A);
        var unitB = Direction(angle.Vertex, angle.B);
        var start = new Point(
            angle.Vertex.X + unitA.X * radius,
            angle.Vertex.Y + unitA.Y * radius);
        var end = new Point(
            angle.Vertex.X + unitB.X * radius,
            angle.Vertex.Y + unitB.Y * radius);
        var cross = unitA.X * unitB.Y - unitA.Y * unitB.X;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                0,
                false,
                cross >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
        }

        NoteLayer.Children.Add(new Path
        {
            Data = geometry,
            Stroke = brush,
            StrokeThickness = angle.Width,
            StrokeLineCap = PenLineCap.Flat,
            IsHitTestVisible = false
        });

        if (string.IsNullOrEmpty(angle.Label))
            return;

        var bisector = Bisector(unitA, unitB);
        var center = new Point(
            angle.Vertex.X + bisector.X * (radius + 18),
            angle.Vertex.Y + bisector.Y * (radius + 18));

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B3000000")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = angle.Label,
                FontSize = 11,
                Foreground = Brushes.White
            }
        };

        pill.Measure(Size.Infinity);
        Canvas.SetLeft(pill, center.X - pill.DesiredSize.Width / 2);
        Canvas.SetTop(pill, center.Y - pill.DesiredSize.Height / 2);
        NoteLayer.Children.Add(pill);
    }

    private void AddBracketPreview(BracketAnnotation bracket)
    {
        var points = AnnotationGeometry.BracketPoints(bracket);
        if (points.Length < 2)
            return;

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromUInt32(bracket.Color), bracket.Opacity),
            StrokeThickness = bracket.Width,
            StrokeLineCap = PenLineCap.Flat,
            StrokeJoin = PenLineJoin.Miter,
            Points = new List<Point>(points)
        };

        ApplyDash(polyline, bracket.Dash);
        NoteLayer.Children.Add(polyline);
    }

    /// <summary>序号徽章预览：和导出共用 NumberGeometry，两边形状才对得上。</summary>
    private void AddNumberBadgePreview(NumberAnnotation number, Rect bounds)
    {
        var fill = new SolidColorBrush(Color.FromUInt32(number.Color));
        var outlineOnly = number.Shape == NumberShape.OutlineCircle;

        void AddShape(Avalonia.Controls.Shapes.Shape shape, Rect box, IBrush? brush, double thickness)
        {
            shape.Width = box.Width;
            shape.Height = box.Height;
            shape.Fill = brush;
            shape.Stroke = outlineOnly ? fill : null;
            shape.StrokeThickness = thickness;
            shape.IsHitTestVisible = false;

            Canvas.SetLeft(shape, box.X);
            Canvas.SetTop(shape, box.Y);
            NoteLayer.Children.Add(shape);
        }

        static Rect Inset(Rect box, double amount) => new(
            box.X + amount, box.Y + amount,
            box.Width - amount * 2, box.Height - amount * 2);

        var polygon = NumberGeometry.Polygon(number.Shape, bounds);
        if (polygon is not null)
        {
            NoteLayer.Children.Add(new Polygon
            {
                Points = polygon.ToList(),
                Fill = outlineOnly ? null : fill,
                Stroke = outlineOnly ? fill : null,
                StrokeThickness = 2,
                IsHitTestVisible = false
            });
            return;
        }

        var radius = Math.Min(bounds.Width, bounds.Height) / 2;

        switch (number.Shape)
        {
            case NumberShape.OutlineCircle:
                AddShape(new Ellipse(), bounds, null, 2);
                break;

            case NumberShape.DoubleCircle:
                AddShape(new Ellipse(), bounds, null, 1.5);
                AddShape(new Ellipse(), Inset(bounds, radius * 0.2), fill, 0);
                break;

            case NumberShape.SolidSquare:
                AddShape(new Rectangle(), bounds, fill, 0);
                break;

            case NumberShape.RoundedSquare:
                AddShape(new Rectangle { RadiusX = radius * 0.3, RadiusY = radius * 0.3 }, bounds, fill, 0);
                break;

            case NumberShape.Capsule:
                AddShape(new Rectangle { RadiusX = radius, RadiusY = radius }, bounds, fill, 0);
                break;

            case NumberShape.Pin:
            {
                var (center, headRadius, tip, left, right) = NumberGeometry.Pin(bounds);

                var head = new Ellipse
                {
                    Width = headRadius * 2,
                    Height = headRadius * 2,
                    Fill = fill,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(head, center.X - headRadius);
                Canvas.SetTop(head, center.Y - headRadius);
                NoteLayer.Children.Add(head);

                NoteLayer.Children.Add(new Polygon
                {
                    Points = new List<Point> { tip, left, right },
                    Fill = fill,
                    IsHitTestVisible = false
                });
                break;
            }

            default:
                AddShape(new Ellipse(), bounds, fill, 0);
                break;
        }
    }

    /// <summary>组合详情区预览：引导线折线 / 箭头 / 虚线框 / 高亮底。</summary>
    private void AddNumberDetailPreview(NumberAnnotation number)
    {
        switch (number.Combo)
        {
            case NumberCombo.Leader:
            {
                var points = NumberGeometry.LeaderPoints(number);
                var brush = new SolidColorBrush(Color.FromUInt32(number.Color));

                NoteLayer.Children.Add(new Polyline
                {
                    Points = points.ToList(),
                    Stroke = brush,
                    StrokeThickness = 1.5,
                    StrokeDashArray = [6, 4],
                    IsHitTestVisible = false
                });

                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = brush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, points[^1].X - 3);
                Canvas.SetTop(dot, points[^1].Y - 3);
                NoteLayer.Children.Add(dot);
                break;
            }

            case NumberCombo.Arrow:
            {
                var badge = NumberGeometry.Bounds(number);
                var center = badge.Center;
                var target = NumberGeometry.TargetOf(number);
                var dx = target.X - center.X;
                var dy = target.Y - center.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);

                if (length < number.Size * 0.5)
                    break;

                var start = new Point(
                    center.X + dx / length * (badge.Width / 2 + 2),
                    center.Y + dy / length * (badge.Height / 2 + 2));

                var brush = new SolidColorBrush(Color.FromUInt32(number.Color));
                AddPreviewLine(brush, start, target, 2);

                var (left, right) = ArrowGeometry.Head(start, target, 8);
                AddPreviewLine(brush, target, left, 2);
                AddPreviewLine(brush, target, right, 2);
                break;
            }

            case NumberCombo.Text:
            {
                var detail = NumberGeometry.DetailBounds(number);
                if (detail.Width < 4 || detail.Height < 4)
                    break;

                var placeholder = string.IsNullOrWhiteSpace(number.Text);

                var box = new Border
                {
                    Width = detail.Width,
                    Height = detail.Height,
                    CornerRadius = new CornerRadius(
                        Math.Min(number.Size * 0.2, Math.Min(detail.Width, detail.Height) / 2)),
                    Background = new SolidColorBrush(Color.FromUInt32(number.Color), 0.9),
                    Child = new TextBlock
                    {
                        Text = placeholder ? "说明" : number.Text,
                        FontSize = number.TextSize,
                        Foreground = new SolidColorBrush(Colors.White, placeholder ? 0.66 : 1),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(box, detail.X);
                Canvas.SetTop(box, detail.Y);
                NoteLayer.Children.Add(box);
                break;
            }

            case NumberCombo.Magnify:
            {
                var detail = NumberGeometry.DetailBounds(number);
                if (detail.Width < 6 || detail.Height < 6)
                    break;

                var badge = NumberGeometry.Bounds(number);
                var brush = new SolidColorBrush(Color.FromUInt32(number.Color));

                AddPreviewLine(brush, badge.Center, detail.Center, 1.5);

                var lens = new Ellipse
                {
                    Width = detail.Width,
                    Height = detail.Height,
                    Stroke = brush,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Colors.Black, 0.25),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(lens, detail.X);
                Canvas.SetTop(lens, detail.Y);
                NoteLayer.Children.Add(lens);

                var zoomLabel = new TextBlock
                {
                    Text = $"{number.Zoom:0.#}×",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Colors.White),
                    Width = detail.Width,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(zoomLabel, detail.X);
                Canvas.SetTop(zoomLabel, detail.Center.Y - 8);
                NoteLayer.Children.Add(zoomLabel);
                break;
            }

            case NumberCombo.DashedBox:
            case NumberCombo.Highlight:
            {
                var detail = NumberGeometry.DetailBounds(number);
                if (detail.Width < 4 || detail.Height < 4)
                    break;

                var color = Color.FromUInt32(number.Color);
                var dashed = number.Combo == NumberCombo.DashedBox;
                var radius = Math.Min(number.Size * 0.2, Math.Min(detail.Width, detail.Height) / 2);

                var box = new Rectangle
                {
                    Width = detail.Width,
                    Height = detail.Height,
                    RadiusX = radius,
                    RadiusY = radius,
                    Fill = new SolidColorBrush(color, dashed ? 0.08 : 0.18),
                    Stroke = dashed ? new SolidColorBrush(color, 0.6) : null,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };

                if (dashed)
                    box.StrokeDashArray = [6, 4];

                Canvas.SetLeft(box, detail.X);
                Canvas.SetTop(box, detail.Y);
                NoteLayer.Children.Add(box);
                break;
            }
        }
    }

    /// <summary>预览层的填充画刷：斜线/交叉斜线在预览里退化为半透明纯色。</summary>
    private static IBrush? PreviewShapeFill(ShapeAnnotation shape) =>
        shape.Fill == ShapeFill.None
            ? null
            : new SolidColorBrush(
                Color.FromUInt32(AnnotationStyle.FillColor(shape.Color, shape.FillColor)),
                AnnotationStyle.FillAlpha(shape.Fill, shape.Opacity));

    /// <summary>气泡预览：尾三角（有填充时）→ 圆角矩形 → 尾两条边，顺序保证接缝被盖住。</summary>
    private void AddBubblePreview(SpeechBubbleAnnotation bubble)
    {
        var rect = bubble.Rect;
        if (rect.Width < 4 || rect.Height < 4)
            return;

        var color = Color.FromUInt32(bubble.Color);
        var fillColor = Color.FromUInt32(AnnotationStyle.FillColor(bubble.Color, bubble.FillColor));
        var fillAlpha = AnnotationStyle.FillAlpha(bubble.Fill, 1);
        var (first, second) = AnnotationGeometry.BubbleTailBase(rect, bubble.Tail);

        if (bubble.Fill != ShapeFill.None)
        {
            NoteLayer.Children.Add(new Polygon
            {
                Points = new List<Point> { first, bubble.Tail, second },
                Fill = new SolidColorBrush(fillColor, fillAlpha),
                IsHitTestVisible = false
            });
        }

        var body = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            CornerRadius = new CornerRadius(Math.Min(10, Math.Min(rect.Width, rect.Height) / 2)),
            BorderThickness = new Thickness(bubble.Width),
            BorderBrush = new SolidColorBrush(color, bubble.Opacity),
            Background = bubble.Fill == ShapeFill.None
                ? null
                : new SolidColorBrush(fillColor, fillAlpha),
            Padding = new Thickness(8, 6),
            IsHitTestVisible = false
        };

        if (!string.IsNullOrEmpty(bubble.Text))
        {
            body.Child = new TextBlock
            {
                Text = bubble.Text,
                FontSize = bubble.FontSize,
                Foreground = bubble.Fill == ShapeFill.Solid
                    ? Brushes.White
                    : new SolidColorBrush(color),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
        }

        Canvas.SetLeft(body, rect.X);
        Canvas.SetTop(body, rect.Y);
        NoteLayer.Children.Add(body);

        // 尾巴只画两条边，底边不画：接缝处由矩形边框盖住
        var stroke = new SolidColorBrush(color, bubble.Opacity);
        AddPreviewLine(stroke, first, bubble.Tail, bubble.Width);
        AddPreviewLine(stroke, bubble.Tail, second, bubble.Width);
    }

    private void AddPreviewLine(IBrush brush, Point from, Point to, double width, LineDash dash = LineDash.Solid)
    {
        var line = new Line
        {
            StartPoint = from,
            EndPoint = to,
            Stroke = brush,
            StrokeThickness = width,
            StrokeLineCap = PenLineCap.Round
        };

        ApplyDash(line, dash);
        NoteLayer.Children.Add(line);
    }

    /// <summary>预览层的虚线 / 点线：dash 长度按线宽倍数给，和导出保持一致。</summary>
    private static void ApplyDash(Shape shape, LineDash dash)
    {
        // 手绘笔触在预览层退化为实线（Avalonia Shape 表达不了抖动几何）
        if (dash == LineDash.Solid)
            return;

        shape.StrokeDashArray = dash == LineDash.Dot ? [0.1, 1.8] : [3, 2];
    }

    private Rect ToDip(PixelRect bounds) => new(
        (bounds.X - _origin.X) / _scaling,
        (bounds.Y - _origin.Y) / _scaling,
        bounds.Width / _scaling,
        bounds.Height / _scaling);

    /// <summary>选区所在的显示器（DIP，相对截图窗口）；无头测试或取不到时返回 null。</summary>
    private Rect? ActiveScreenDip() => ScreenDipFor(_selectionOrigin);

    /// <summary>指定矩形中心的显示器（DIP，相对截图窗口）。</summary>
    private Rect? ScreenDipFor(Rect rect)
    {
        var center = new PixelPoint(
            _origin.X + (int)Math.Round((rect.X + rect.Width / 2) * _scaling),
            _origin.Y + (int)Math.Round((rect.Y + rect.Height / 2) * _scaling));

        try
        {
            return Screens.ScreenFromPoint(center) is { } screen
                ? ToDip(screen.Bounds)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
