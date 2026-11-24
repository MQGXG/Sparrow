// CaptureWindow 分部类：标注命中测试、拖动/缩放/旋转、几何辅助与工具切换。
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
    /// <summary>指针下面最上面的一条标注；没有返回 -1。</summary>
    private int HitNote(Point point)
    {
        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);

        for (var index = _notes.Items.Count - 1; index >= 0; index--)
        {
            if (HitTestNote(_notes.Items[index], local))
                return index;
        }

        return -1;
    }

    /// <summary>橡皮擦：擦掉指针下最上面的一条标注。</summary>
    private void EraseAt(Point point)
    {
        var hit = HitNote(point);
        if (hit < 0)
            return;

        _notes.RemoveAt(hit);
        MarkNoteMutation();

        if (_selectedNote == hit)
            _selectedNote = -1;
        else if (_selectedNote > hit)
            _selectedNote--;

        RebuildNotePreview();
        UpdateToolButtons();
    }

    /// <summary>开始拖动选中标注的缩放控制点。</summary>
    private void BeginNoteResize(Point point, ResizeHandle handle)
    {
        _mode = DragMode.NoteResize;
        _noteResizeHandle = handle;
        _noteResizeIndex = _selectedNote;
        _noteResizeOriginal = _notes.Items[_selectedNote];
        _noteResizeBounds = NoteBounds(_noteResizeOriginal);
        _noteResizeMoved = false;
        HideTextEditor();
    }

    /// <summary>按控制点缩放标注；文字不显示控制点，其余类型按包围盒等比映射。</summary>
    private void ApplyNoteResize(Point point)
    {
        if (_noteResizeOriginal is null
            || _noteResizeIndex < 0
            || _noteResizeIndex >= _notes.Items.Count)
            return;

        var current = new Point(point.X - _selection.X, point.Y - _selection.Y);
        var rect = _noteResizeBounds;
        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        if (_noteResizeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW)
            left = Math.Min(current.X, right - MinNoteSize);
        if (_noteResizeHandle is ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE)
            right = Math.Max(current.X, left + MinNoteSize);
        if (_noteResizeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE)
            top = Math.Min(current.Y, bottom - MinNoteSize);
        if (_noteResizeHandle is ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE)
            bottom = Math.Max(current.Y, top + MinNoteSize);

        left = Math.Clamp(left, 0, Math.Max(0, _selection.Width - MinNoteSize));
        top = Math.Clamp(top, 0, Math.Max(0, _selection.Height - MinNoteSize));
        right = Math.Clamp(right, left + MinNoteSize, Math.Max(MinNoteSize, _selection.Width));
        bottom = Math.Clamp(bottom, top + MinNoteSize, Math.Max(MinNoteSize, _selection.Height));

        var target = new Rect(left, top, right - left, bottom - top);
        if (target.Width < MinNoteSize || target.Height < MinNoteSize)
            return;

        if (!_noteResizeMoved)
        {
            _noteResizeMoved = true;
            MarkNoteMutation();
        }

        var scaled = AnnotationGeometry.Scale(_noteResizeOriginal, _noteResizeBounds, target);
        if (scaled is DistanceAnnotation distance)
            scaled = distance with { Label = FormatDistance(distance.From, distance.To) };

        _notes.Replace(_noteResizeIndex, scaled);
        RebuildNotePreview();
    }

    /// <summary>开始拖动旋转手柄。</summary>
    private void BeginNoteRotate(Point point)
    {
        _mode = DragMode.NoteRotate;
        _noteRotateIndex = _selectedNote;
        _noteRotateOriginal = _notes.Items[_selectedNote];
        _noteRotateCenter = NoteRotationCenter(_noteRotateOriginal);
        _noteRotateBase = AnnotationGeometry.RotationOf(_noteRotateOriginal);
        _noteRotateStartVector = new Point(
            point.X - _selection.X - _noteRotateCenter.X,
            point.Y - _selection.Y - _noteRotateCenter.Y);
        _noteRotateMoved = false;
        HideTextEditor();
    }

    /// <summary>按鼠标相对中心的角度设置旋转；按住 Shift 吸附 15°。</summary>
    private void ApplyNoteRotate(Point point, bool snap)
    {
        if (_noteRotateOriginal is null
            || _noteRotateIndex < 0
            || _noteRotateIndex >= _notes.Items.Count)
            return;

        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);
        var vector = new Point(
            local.X - _noteRotateCenter.X,
            local.Y - _noteRotateCenter.Y);

        if (Math.Abs(vector.X) < 0.001 && Math.Abs(vector.Y) < 0.001)
            return;

        var startAngle = Math.Atan2(_noteRotateStartVector.Y, _noteRotateStartVector.X);
        var currentAngle = Math.Atan2(vector.Y, vector.X);
        var angle = _noteRotateBase + (currentAngle - startAngle) * 180 / Math.PI;

        if (snap)
            angle = Math.Round(angle / 15) * 15;

        angle = NormalizeAngle(angle);

        if (!_noteRotateMoved)
        {
            _noteRotateMoved = true;
            MarkNoteMutation();
        }

        _notes.Replace(
            _noteRotateIndex,
            AnnotationGeometry.RotateTo(_noteRotateOriginal, angle));
        RebuildNotePreview();
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle < -180) angle += 360;
        return angle;
    }

    private void ApplyNoteDrag(Point point)    {        if (_draggingNote < 0 || _noteDragOriginal is null || _draggingNote >= _notes.Items.Count)
            return;

        // 鼠标点换算成选区相对坐标，位移直接加在原始图元上
        var dx = point.X - _selection.X - _noteDragStart.X;
        var dy = point.Y - _selection.Y - _noteDragStart.Y;

        if (!_noteDragMoved && (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01))
        {
            _noteDragMoved = true;
            MarkNoteMutation();
        }

        _notes.Replace(_draggingNote, TranslateNote(_noteDragOriginal, dx, dy));
        RebuildNotePreview();
    }

    /// <summary>删除选中的标注（Delete / Backspace）。</summary>
    internal void DeleteSelectedNote()
    {
        if (_selectedNote < 0 || _selectedNote >= _notes.Items.Count)
            return;

        _notes.RemoveAt(_selectedNote);
        _selectedNote = -1;
        MarkNoteMutation();
        RebuildNotePreview();
        UpdateToolButtons();
    }

    private static IAnnotation TranslateNote(IAnnotation item, double dx, double dy) => item switch
    {
        ShapeAnnotation shape => shape with
        {
            Rect = new Rect(shape.Rect.X + dx, shape.Rect.Y + dy, shape.Rect.Width, shape.Rect.Height)
        },
        ArrowAnnotation arrow => arrow with
        {
            From = new Point(arrow.From.X + dx, arrow.From.Y + dy),
            To = new Point(arrow.To.X + dx, arrow.To.Y + dy)
        },
        LineAnnotation line => line with
        {
            From = new Point(line.From.X + dx, line.From.Y + dy),
            To = new Point(line.To.X + dx, line.To.Y + dy)
        },
        DistanceAnnotation distance => distance with
        {
            From = new Point(distance.From.X + dx, distance.From.Y + dy),
            To = new Point(distance.To.X + dx, distance.To.Y + dy)
        },
        AngleAnnotation angle => angle with
        {
            Vertex = new Point(angle.Vertex.X + dx, angle.Vertex.Y + dy),
            A = new Point(angle.A.X + dx, angle.A.Y + dy),
            B = new Point(angle.B.X + dx, angle.B.Y + dy)
        },
        BracketAnnotation bracket => bracket with
        {
            Rect = new Rect(
                bracket.Rect.X + dx, bracket.Rect.Y + dy,
                bracket.Rect.Width, bracket.Rect.Height)
        },
        SpeechBubbleAnnotation bubble => bubble with
        {
            Rect = new Rect(
                bubble.Rect.X + dx, bubble.Rect.Y + dy,
                bubble.Rect.Width, bubble.Rect.Height),
            Tail = new Point(bubble.Tail.X + dx, bubble.Tail.Y + dy)
        },
        PenAnnotation pen => pen with
        {
            Points = pen.Points.Select(point => new Point(point.X + dx, point.Y + dy)).ToList()
        },
        MosaicAnnotation mosaic => mosaic with
        {
            Rect = new Rect(mosaic.Rect.X + dx, mosaic.Rect.Y + dy, mosaic.Rect.Width, mosaic.Rect.Height)
        },
        SpotlightAnnotation spotlight => spotlight with
        {
            Rect = new Rect(
                spotlight.Rect.X + dx, spotlight.Rect.Y + dy,
                spotlight.Rect.Width, spotlight.Rect.Height)
        },
        MagnifierAnnotation magnifier => magnifier with
        {
            Rect = new Rect(
                magnifier.Rect.X + dx, magnifier.Rect.Y + dy,
                magnifier.Rect.Width, magnifier.Rect.Height)
        },
        WatermarkAnnotation watermark => watermark with
        {
            Area = new Rect(
                watermark.Area.X + dx, watermark.Area.Y + dy,
                watermark.Area.Width, watermark.Area.Height)
        },
        TextAnnotation text => text with
        {
            Origin = new Point(text.Origin.X + dx, text.Origin.Y + dy)
        },
        NumberAnnotation number => number with
        {
            Center = new Point(number.Center.X + dx, number.Center.Y + dy)
        },
        _ => item
    };

    /// <summary>命中测试：空心形状要求靠近边框，实心形状整块可点。</summary>
    private static bool HitTestNote(IAnnotation item, Point point)
    {
        const double slop = 6;

        var rotation = AnnotationGeometry.RotationOf(item);
        if (Math.Abs(rotation) > 0.01)
            point = RotatePoint(point, NoteRotationCenter(item), -rotation);

        switch (item)
        {
            case ShapeAnnotation shape:
                var outer = Grow(shape.Rect, shape.Width / 2 + slop);

                if (shape.Fill != ShapeFill.None)
                    return outer.Contains(point);

                // 空心：中间留空，点进去是移动选区 / 画新东西
                var inner = Grow(shape.Rect, -(shape.Width / 2 + slop));
                return outer.Contains(point)
                    && (inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(point));

            case ArrowAnnotation arrow:
                return DistanceToSegment(point, arrow.From, arrow.To) <= arrow.Width / 2 + slop;

            case LineAnnotation line:
                return DistanceToSegment(point, line.From, line.To) <= line.Width / 2 + slop;

            case DistanceAnnotation distance:
                return DistanceToSegment(point, distance.From, distance.To)
                       <= distance.Width / 2 + slop;

            case AngleAnnotation angle:
                return DistanceToSegment(point, angle.Vertex, angle.A) <= angle.Width / 2 + slop
                       || DistanceToSegment(point, angle.Vertex, angle.B) <= angle.Width / 2 + slop;

            case BracketAnnotation bracket:
                var bracketPoints = AnnotationGeometry.BracketPoints(bracket);
                for (var index = 1; index < bracketPoints.Length; index++)
                {
                    if (DistanceToSegment(point, bracketPoints[index - 1], bracketPoints[index])
                        <= bracket.Width / 2 + slop)
                        return true;
                }

                return false;

            case SpeechBubbleAnnotation bubble:
            {
                if (bubble.Rect.Contains(point))
                    return true;

                var (first, second) = AnnotationGeometry.BubbleTailBase(bubble.Rect, bubble.Tail);
                return DistanceToSegment(point, first, bubble.Tail) <= bubble.Width / 2 + slop
                       || DistanceToSegment(point, bubble.Tail, second) <= bubble.Width / 2 + slop;
            }

            case PenAnnotation pen:
                for (var index = 1; index < pen.Points.Count; index++)
                {
                    if (DistanceToSegment(point, pen.Points[index - 1], pen.Points[index])
                        <= pen.Width / 2 + slop)
                        return true;
                }

                return false;

            case MosaicAnnotation mosaic:
                return mosaic.Rect.Contains(point);

            case SpotlightAnnotation spotlight:
                return spotlight.Rect.Contains(point);

            case MagnifierAnnotation magnifier:
                return NoteBounds(magnifier).Contains(point);

            case WatermarkAnnotation watermark:
                return WatermarkLayout.Hit(watermark, point);

            case TextAnnotation text:
                return NoteBounds(text).Contains(point);

            case NumberAnnotation number:
            {
                // 徽章可能比 Size 大：胶囊 1.5×、定位针 1.3×、自定义标签按估宽撑开
                var halfWidth = number.Shape == NumberShape.Capsule
                    ? number.Size * 0.75
                    : number.Size / 2;
                var halfHeight = number.Shape == NumberShape.Pin
                    ? number.Size * 0.65
                    : number.Size / 2;

                var label = NumberText.Format(number.Number, number.Format, number.Label);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    var textWidth = EstimateTextWidth(label, number.Size * 0.55);
                    halfWidth = Math.Max(halfWidth, textWidth / 2 + number.Size * 0.45);
                }

                if (Math.Abs(point.X - number.Center.X) <= halfWidth + slop
                    && Math.Abs(point.Y - number.Center.Y) <= halfHeight + slop)
                {
                    return true;
                }

                // 详情区 / 引导线 / 箭头点进去也是选中整条，拖动整体平移
                switch (number.Combo)
                {
                    case NumberCombo.DashedBox:
                    case NumberCombo.Highlight:
                        return NumberGeometry.DetailBounds(number).Contains(point);

                    case NumberCombo.Leader:
                    {
                        var points = NumberGeometry.LeaderPoints(number);

                        for (var index = 1; index < points.Count; index++)
                        {
                            if (DistanceToSegment(point, points[index - 1], points[index]) <= slop)
                                return true;
                        }

                        return false;
                    }

                    case NumberCombo.Arrow:
                        return DistanceToSegment(
                            point,
                            NumberGeometry.Bounds(number).Center,
                            NumberGeometry.TargetOf(number)) <= slop;

                    default:
                        return false;
                }
            }

            default:
                return false;
        }
    }

    private static readonly ResizeHandle[] NoteResizeHandles =
    [
        ResizeHandle.NW, ResizeHandle.N, ResizeHandle.NE, ResizeHandle.E,
        ResizeHandle.SE, ResizeHandle.S, ResizeHandle.SW, ResizeHandle.W
    ];

    private static Point NoteHandlePoint(Rect bounds, ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW => new Point(bounds.Left, bounds.Top),
        ResizeHandle.N => new Point(bounds.Center.X, bounds.Top),
        ResizeHandle.NE => new Point(bounds.Right, bounds.Top),
        ResizeHandle.E => new Point(bounds.Right, bounds.Center.Y),
        ResizeHandle.SE => new Point(bounds.Right, bounds.Bottom),
        ResizeHandle.S => new Point(bounds.Center.X, bounds.Bottom),
        ResizeHandle.SW => new Point(bounds.Left, bounds.Bottom),
        ResizeHandle.W => new Point(bounds.Left, bounds.Center.Y),
        _ => new Point(bounds.Center.X, bounds.Center.Y)
    };

    private static Point NoteRotateHandlePoint(Rect bounds)
    {
        var y = bounds.Top - RotateHandleDistance;
        if (y < 0)
            y = bounds.Bottom + RotateHandleDistance;

        return new Point(bounds.Center.X, y);
    }

    /// <summary>命中旋转手柄（局部坐标）。</summary>
    private bool HitNoteRotateHandle(Point point)
    {
        if (_tool != AnnotationTool.None
            || _selectedNote < 0
            || _selectedNote >= _notes.Items.Count)
            return false;

        var note = _notes.Items[_selectedNote];
        if (!AnnotationGeometry.IsRotatable(note))
            return false;

        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);
        return Distance(local, NoteRotateHandlePoint(NoteBounds(note))) <= 7;
    }

    /// <summary>命中选中标注的缩放控制点（局部坐标）。</summary>
    private ResizeHandle HitNoteHandle(Point point)
    {
        if (_tool != AnnotationTool.None
            || _selectedNote < 0
            || _selectedNote >= _notes.Items.Count)
            return ResizeHandle.None;

        var note = _notes.Items[_selectedNote];
        if (!AnnotationGeometry.IsScalable(note))
            return ResizeHandle.None;

        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);
        var bounds = NoteBounds(note);

        foreach (var handle in NoteResizeHandles)
        {
            if (Distance(local, NoteHandlePoint(bounds, handle)) <= 7)
                return handle;
        }

        return ResizeHandle.None;
    }

    /// <summary>选中标注的 8 个缩放点；文字不显示。</summary>
    /// <summary>命中序号组合的目标点把手（局部坐标）。</summary>
    private bool HitNumberTargetHandle(Point point)
    {
        if (_selectedNote < 0 || _selectedNote >= _notes.Items.Count)
            return false;

        if (_notes.Items[_selectedNote] is not NumberAnnotation { Combo: not NumberCombo.Plain } number)
            return false;

        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);
        return Distance(local, NumberGeometry.TargetOf(number)) <= 7;
    }

    /// <summary>拖动目标点：清掉显式详情区，让引导线/箭头/框跟着重新推导。</summary>
    private void ApplyNumberTarget(Point point)
    {
        if (_selectedNote < 0 || _selectedNote >= _notes.Items.Count)
            return;

        var local = new Point(
            Math.Clamp(point.X - _selection.X, 0, _selection.Width),
            Math.Clamp(point.Y - _selection.Y, 0, _selection.Height));

        _notes.Replace(
            _selectedNote,
            AnnotationStyler.WithNumberTarget(_notes.Items[_selectedNote], local));

        _noteDragMoved = true;
        RebuildNotePreview();
        UpdateNoteHandles();
    }

    private void UpdateNoteHandles()
    {
        NoteHandleLayer.Children.Clear();
        NoteHandleLayer.IsVisible = false;

        if (_selectedNote < 0 || _selectedNote >= _notes.Items.Count)
            return;

        var note = _notes.Items[_selectedNote];
        if (!AnnotationGeometry.IsScalable(note))
            return;

        NoteHandleLayer.Width = _selection.Width;
        NoteHandleLayer.Height = _selection.Height;
        Canvas.SetLeft(NoteHandleLayer, _selection.X);
        Canvas.SetTop(NoteHandleLayer, _selection.Y);

        var bounds = NoteBounds(note);
        foreach (var handle in NoteResizeHandles)
        {
            var point = NoteHandlePoint(bounds, handle);
            var dot = new Rectangle
            {
                Width = 7,
                Height = 7,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.Parse("#FFE5A44C")),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(dot, point.X - 3.5);
            Canvas.SetTop(dot, point.Y - 3.5);
            NoteHandleLayer.Children.Add(dot);
        }

        if (AnnotationGeometry.IsRotatable(note))
        {
            var rotate = NoteRotateHandlePoint(bounds);
            var anchor = rotate.Y < bounds.Top
                ? new Point(bounds.Center.X, bounds.Top)
                : new Point(bounds.Center.X, bounds.Bottom);
            var stem = new Line
            {
                StartPoint = anchor,
                EndPoint = rotate,
                Stroke = new SolidColorBrush(Color.Parse("#B3E5A44C")),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            NoteHandleLayer.Children.Add(stem);

            var knob = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.Parse("#FFE5A44C")),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(knob, rotate.X - 4.5);
            Canvas.SetTop(knob, rotate.Y - 4.5);
            NoteHandleLayer.Children.Add(knob);
        }

        // 序号组合的目标点：一个可拖的实心把手
        if (note is NumberAnnotation { Combo: not NumberCombo.Plain } numberNote)
        {
            var target = NumberGeometry.TargetOf(numberNote);
            var targetDot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Color.Parse("#FFE5A44C")),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(targetDot, target.X - 4.5);
            Canvas.SetTop(targetDot, target.Y - 4.5);
            NoteHandleLayer.Children.Add(targetDot);
        }

        NoteHandleLayer.IsVisible = true;
    }

    /// <summary>旋转中心；气泡用矩形中心，和导出保持一致（不含尾巴）。</summary>
    private static Point NoteRotationCenter(IAnnotation item) => item switch
    {
        SpeechBubbleAnnotation bubble => bubble.Rect.Center,
        _ => NoteBounds(item).Center
    };

    /// <summary>标注在选区坐标系里的包围盒；选中框和命中测试共用。</summary>
    private static Rect NoteBounds(IAnnotation item)
    {        switch (item)
        {
            case ShapeAnnotation shape:
                return shape.Rect;

            case ArrowAnnotation arrow:
                return FromPoints(arrow.From, arrow.To);

            case LineAnnotation line:
                return FromPoints(line.From, line.To);

            case DistanceAnnotation distance:
                return FromPoints(distance.From, distance.To);

            case AngleAnnotation angle:
                return FromPoints([angle.Vertex, angle.A, angle.B]);

            case BracketAnnotation bracket:
                return bracket.Rect;

            case SpeechBubbleAnnotation bubble:
                return FromPoints(
                    [bubble.Rect.TopLeft, bubble.Rect.BottomRight, bubble.Tail]);

            case PenAnnotation { Points.Count: > 0 } pen:
                return FromPoints(pen.Points);

            case MosaicAnnotation mosaic:
                return mosaic.Rect;

            case SpotlightAnnotation spotlight:
                return spotlight.Rect;

            case MagnifierAnnotation magnifier:
                return magnifier.Rect;

            case WatermarkAnnotation watermark:
                return watermark.Area;

            case TextAnnotation text:
            {
                // 没有排版信息，按字宽粗估：中日韩一个字号宽，西文约 0.6
                var width = EstimateTextWidth(text.Text, text.FontSize);
                var left = text.Align switch
                {
                    TextAlign.Center => text.Origin.X - width / 2,
                    TextAlign.Right => text.Origin.X - width,
                    _ => text.Origin.X
                };

                return new Rect(left, text.Origin.Y - text.FontSize, width, text.FontSize * 1.25);
            }

            case NumberAnnotation number:
            {
                var badge = NumberGeometry.Bounds(number);

                return number.Combo switch
                {
                    NumberCombo.DashedBox or NumberCombo.Highlight
                        => badge.Union(NumberGeometry.DetailBounds(number)),

                    // 引导线 / 箭头：把目标点也包进选择框
                    NumberCombo.Leader or NumberCombo.Arrow
                        => badge.Union(TargetRect(NumberGeometry.TargetOf(number))),

                    _ => badge
                };
            }

            default:
                return default;
        }
    }

    private static double EstimateTextWidth(string text, double fontSize)
    {
        var width = 0.0;

        foreach (var ch in text)
            width += ch > 0x2E7F ? fontSize : fontSize * 0.6;

        return Math.Max(fontSize, width);
    }

    private static Rect Grow(Rect rect, double amount) => new(
        rect.X - amount,
        rect.Y - amount,
        Math.Max(0, rect.Width + amount * 2),
        Math.Max(0, rect.Height + amount * 2));

    private static Rect FromPoints(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    private static Rect FromPoints(IReadOnlyList<Point> points)
    {
        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;

        foreach (var point in points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>点到线段的最短距离。</summary>
    private static double DistanceToSegment(Point point, Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared < 0.0001)
            return Distance(point, from);

        var t = Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared, 0, 1);

        return Distance(point, new Point(from.X + dx * t, from.Y + dy * t));
    }

    /// <summary>数字键 → 工具；和按钮 ToolTip 里的数字保持一致。L 给直线。</summary>
    private static bool TryMapToolKey(Key key, out AnnotationTool tool)
    {
        tool = key switch
        {
            Key.D2 or Key.NumPad2 => AnnotationTool.Rectangle,
            Key.D3 or Key.NumPad3 => AnnotationTool.Ellipse,
            Key.D4 or Key.NumPad4 => AnnotationTool.Arrow,
            Key.D5 or Key.NumPad5 => AnnotationTool.Pen,
            Key.D6 or Key.NumPad6 => AnnotationTool.Highlighter,
            Key.D7 or Key.NumPad7 => AnnotationTool.Mosaic,
            Key.D8 or Key.NumPad8 => AnnotationTool.Text,
            Key.D9 or Key.NumPad9 => AnnotationTool.Number,
            Key.L => AnnotationTool.Line,
            Key.D => AnnotationTool.Distance,
            Key.E => AnnotationTool.Eraser,
            Key.K => AnnotationTool.Spotlight,
            Key.Z => AnnotationTool.Magnifier,
            Key.W => AnnotationTool.Watermark,
            Key.Q => AnnotationTool.Bubble,
            Key.OemOpenBrackets => AnnotationTool.Bracket,
            _ => AnnotationTool.None
        };

        return tool != AnnotationTool.None;
    }

    /// <summary>选区变化或重设时清空标注：坐标相对选区，选区一动旧标注就错位。</summary>
    private void ClearAnnotations()
    {
        HideTextEditor();
        MarkNoteMutation();
        CancelAngleDraft();

        if (_notes.Items.Count == 0 && _pendingAnnotation is null)
            return;

        _notes.Clear();
        _selectedNote = -1;
        _draggingNote = -1;
        _noteDragOriginal = null;
        _pendingAnnotation = null;
        _penPoints = null;
        _nextNumber = 1;
        _notesBeforeDrag = null;
        RebuildNotePreview();
        UpdateToolButtons();
    }
}
