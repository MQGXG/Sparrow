// CaptureWindow 分部类：开始/更新/提交标注，角度的拖拽与提示。
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
    /// <summary>工具激活时在选区内按下 = 开始画；返回 true 表示这次按下已被标注消费。</summary>
    private bool BeginAnnotation(Point point)
    {
        if (_tool == AnnotationTool.None || !_hasSelection || !_selection.Contains(point))
            return false;

        _annotationStart = new Point(point.X - _selection.X, point.Y - _selection.Y);
        _penPoints = null;

        switch (_tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
            case AnnotationTool.Diamond:
                _pendingAnnotation = new ShapeAnnotation(
                    new Rect(_annotationStart, new Size()),
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _tool == AnnotationTool.Ellipse,
                    _noteOpacity,
                    _noteFill,
                    _noteRadius,
                    _noteDash,
                    FillColor: _noteFillColor,
                    Diamond: _tool == AnnotationTool.Diamond,
                    Style: _noteStyle);
                break;

            case AnnotationTool.Arrow:
                _pendingAnnotation = new ArrowAnnotation(
                    _annotationStart,
                    _annotationStart,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _noteOpacity,
                    _noteDash,
                    Style: _noteStyle);
                break;

            case AnnotationTool.Line:
                _pendingAnnotation = new LineAnnotation(
                    _annotationStart,
                    _annotationStart,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _noteOpacity,
                    _noteDash,
                    Style: _noteStyle);
                break;

            case AnnotationTool.Distance:
                _pendingAnnotation = new DistanceAnnotation(
                    _annotationStart,
                    _annotationStart,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    FormatDistance(_annotationStart, _annotationStart),
                    _noteOpacity,
                    _noteDash,
                    Style: _noteStyle);
                break;

            case AnnotationTool.Pen:
                _penPoints = [_annotationStart];
                _pendingAnnotation = new PenAnnotation(
                    _penPoints,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _noteOpacity,
                    _noteDash,
                    Style: _noteStyle);
                break;

            case AnnotationTool.Highlighter:
                _penPoints = [_annotationStart];
                _pendingAnnotation = new PenAnnotation(
                    _penPoints,
                    _noteColor.ToUInt32(),
                    HighlighterWidth,
                    HighlighterOpacity);
                break;

            case AnnotationTool.Mosaic:
                _pendingAnnotation = new MosaicAnnotation(
                    new Rect(_annotationStart, new Size()), _mosaicCell, _mosaicBlur);
                break;

            case AnnotationTool.Spotlight:
                // 聚光灯固定用黑色压暗：用当前标注色会得到“琥珀高亮”，不是压暗
                _pendingAnnotation = new SpotlightAnnotation(
                    new Rect(_annotationStart, new Size()),
                    0xFF000000,
                    _spotlightOpacity);
                break;

            case AnnotationTool.Magnifier:
                _pendingAnnotation = new MagnifierAnnotation(
                    new Rect(_annotationStart, new Size()));
                break;

            case AnnotationTool.Watermark:
                _pendingAnnotation = new WatermarkAnnotation(
                    new Rect(_annotationStart, new Size()),
                    _watermarkText,
                    _noteColor.ToUInt32(),
                    _watermarkFontSize,
                    _watermarkOpacity,
                    WatermarkAngle,
                    WatermarkSpacing);
                break;

            case AnnotationTool.Bracket:
                _pendingAnnotation = new BracketAnnotation(
                    new Rect(_annotationStart, new Size()),
                    true,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _noteOpacity,
                    _noteDash,
                    Style: _noteStyle);
                break;

            case AnnotationTool.Bubble:
                _pendingAnnotation = new SpeechBubbleAnnotation(
                    new Rect(_annotationStart, new Size()),
                    _annotationStart,
                    string.Empty,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _bubbleFontSize,
                    _bubbleFill,
                    FillColor: _bubbleFillColor);
                break;

            case AnnotationTool.Text:
                ShowTextEditor(_annotationStart);
                return true;

            case AnnotationTool.Number:
                // 按下先放 badge，拖出去的目标点在松手时定稿
                _pendingAnnotation = new NumberAnnotation(
                    _annotationStart, _nextNumber, _noteColor.ToUInt32(), _numberSize,
                    Format: _numberFormat,
                    Shape: _numberShape,
                    Combo: _numberCombo,
                    Target: _annotationStart,
                    TextSize: _numberTextSize,
                    Zoom: _numberZoom);
                return true;

            default:
                return false;
        }

        RebuildNotePreview();
        return true;
    }

    private void UpdatePendingAnnotation(Point point)
    {
        // 标注坐标相对选区，超出选区就贴在边上
        var current = new Point(
            Math.Clamp(point.X - _selection.X, 0, _selection.Width),
            Math.Clamp(point.Y - _selection.Y, 0, _selection.Height));

        // 落点吸附到选区边框/中线，方便画满整幅或居中
        var (snapped, guideX, guideY) = SnapGuides.SnapToBox(
            new Point(_selection.X + current.X, _selection.Y + current.Y),
            _selection);
        current = new Point(snapped.X - _selection.X, snapped.Y - _selection.Y);
        ShowGuides(guideX, guideY);

        _pendingAnnotation = _pendingAnnotation switch
        {
            ShapeAnnotation shape => shape with { Rect = NormalizeRect(_annotationStart, current) },
            ArrowAnnotation arrow => arrow with { To = current },
            LineAnnotation line => line with { To = current },
            MosaicAnnotation mosaic => mosaic with { Rect = NormalizeRect(_annotationStart, current) },
            SpotlightAnnotation spotlight => spotlight with
            {
                Rect = NormalizeRect(_annotationStart, current)
            },
            MagnifierAnnotation magnifier => magnifier with
            {
                Rect = NormalizeRect(_annotationStart, current)
            },
            WatermarkAnnotation watermark => watermark with
            {
                Area = NormalizeRect(_annotationStart, current)
            },
            DistanceAnnotation distance => distance with
            {
                To = current,
                Label = FormatDistance(distance.From, current)
            },
            AngleAnnotation angle => angle with
            {
                B = current,
                Label = FormatAngle(angle.Vertex, angle.A, current)
            },
            BracketAnnotation bracket => bracket with
            {
                Rect = NormalizeRect(_annotationStart, current),
                OpenRight = current.X >= _annotationStart.X
            },
            SpeechBubbleAnnotation bubble => bubble with
            {
                Rect = NormalizeRect(_annotationStart, current),
                Tail = AnnotationGeometry.BubbleTail(
                    NormalizeRect(_annotationStart, current), _annotationStart)
            },
            NumberAnnotation number => number with { Target = current },
            PenAnnotation pen when _penPoints is not null => AddPenPoint(pen, current),
            _ => _pendingAnnotation
        };
    }

    private PenAnnotation AddPenPoint(PenAnnotation pen, Point current)
    {
        _penPoints!.Add(current);
        return pen with { Points = _penPoints };
    }

    private static Rect NormalizeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    /// <summary>目标点的 1×1 包围盒，用来把目标点并进选择框。</summary>
    private static Rect TargetRect(Point target) => new(target.X, target.Y, 1, 1);

    private void CommitPendingAnnotation()
    {
        var annotation = _pendingAnnotation;
        _pendingAnnotation = null;
        _penPoints = null;

        if (annotation is null)
            return;

        // 测距标签在提交时按当前单位定稿，之后切单位不回溯
        if (annotation is DistanceAnnotation measured)
            annotation = measured with { Label = FormatDistance(measured.From, measured.To) };

        // 水印单击（没拖出区域）= 铺满整个选区
        if (annotation is WatermarkAnnotation clicked &&
            (clicked.Area.Width < 8 || clicked.Area.Height < 8))
        {
            annotation = clicked with
            {
                Area = new Rect(0, 0, _selection.Width, _selection.Height)
            };
        }

        // 序号：单击也要保留；没拖出目标点时给个默认偏移，并在提交时取号
        if (annotation is NumberAnnotation number)
        {
            var target = number.Target ?? number.Center;

            if (Distance(target, number.Center) < 4)
            {
                var badge = NumberGeometry.BadgeBounds(number.Center, number.Size, number.Shape);
                target = new Point(badge.Right + number.Size * 3, badge.Bottom + number.Size * 1.5);
            }

            annotation = number with { Target = target, Number = _nextNumber++ };
        }

        // 点一下没拖动：当误触丢掉，不进撤销栈
        var keep = annotation switch
        {
            ShapeAnnotation shape => shape.Rect.Width >= 2 && shape.Rect.Height >= 2,
            ArrowAnnotation arrow => Distance(arrow.From, arrow.To) >= 6,
            LineAnnotation line => Distance(line.From, line.To) >= 6,
            PenAnnotation pen => pen.Points.Count >= 2,
            MosaicAnnotation mosaic => mosaic.Rect.Width >= 4 && mosaic.Rect.Height >= 4,
            SpotlightAnnotation spotlight => spotlight.Rect.Width >= 8 && spotlight.Rect.Height >= 8,
            MagnifierAnnotation magnifier => magnifier.Rect.Width >= 8 && magnifier.Rect.Height >= 8,
            WatermarkAnnotation watermark => watermark.Area.Width >= 8 && watermark.Area.Height >= 8,
            DistanceAnnotation distance => Distance(distance.From, distance.To) >= 6,
            BracketAnnotation bracket => bracket.Rect.Width >= 8 && bracket.Rect.Height >= 8,
            SpeechBubbleAnnotation bubble => bubble.Rect.Width >= 8 && bubble.Rect.Height >= 8,
            NumberAnnotation => true,
            _ => false
        };

        if (keep)
        {
            _notes.Add(annotation);
            MarkNoteMutation();

            // 文本标签组合：画完直接进入说明文字编辑（和 LumaShot 一致）
            if (annotation is NumberAnnotation { Combo: NumberCombo.Text })
            {
                var index = _notes.Items.Count - 1;
                SelectNote(index);
                EditNoteText(index);
            }
        }

        RebuildNotePreview();
        UpdateToolButtons();
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

        /// <summary>绕 center 旋转 point（度，屏幕坐标，顺时针为正）。</summary>
    private static Point RotatePoint(Point point, Point center, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;

        return new Point(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);
    }

    private static Point Direction(Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < 0.001 ? new Point(1, 0) : new Point(dx / length, dy / length);
    }

    private static Point Bisector(Point a, Point b)
    {
        var x = a.X + b.X;
        var y = a.Y + b.Y;
        var length = Math.Sqrt(x * x + y * y);
        return length < 0.001 ? new Point(-a.Y, a.X) : new Point(x / length, y / length);
    }

    /// <summary>测距标签：跟随选区工具条的 px/DIP 单位，创建时定稿。</summary>
    private string FormatDistance(Point from, Point to)
    {
        var dip = Distance(from, to);
        return _sizeInPixels ? $"{dip * _scaling:0} px" : $"{dip:0} DIP";
    }

    /// <summary>角度第一步：按下开始第一条边；草稿已存在时开始第二条边。</summary>
    private void BeginAngleDrag(Point point)
    {
        _annotationStart = new Point(point.X - _selection.X, point.Y - _selection.Y);
        _penPoints = null;

        if (_angleDraft.IsActive)
        {
            _anglePhase = AnglePhase.Second;
            _pendingAnnotation = new AngleAnnotation(
                _angleDraft.Vertex!.Value,
                _angleDraft.First!.Value,
                _annotationStart,
                _noteColor.ToUInt32(),
                _noteWidth,
                FormatAngle(_angleDraft.Vertex.Value, _angleDraft.First.Value, _annotationStart),
                _noteOpacity);
        }
        else
        {
            _anglePhase = AnglePhase.First;
            _pendingAnnotation = new LineAnnotation(
                _annotationStart,
                _annotationStart,
                _noteColor.ToUInt32(),
                _noteWidth,
                _noteOpacity,
                _noteDash,
                    Style: _noteStyle);
        }

        RebuildNotePreview();
    }

    /// <summary>角度松手：第一段只存草稿，第二段才真正提交标注。</summary>
    private void CommitAngleDrag()
    {
        var annotation = _pendingAnnotation;
        _pendingAnnotation = null;
        _penPoints = null;
        var phase = _anglePhase;
        _anglePhase = AnglePhase.None;

        if (phase == AnglePhase.First)
        {
            if (annotation is LineAnnotation { } line && Distance(line.From, line.To) >= 6)
            {
                _angleDraft.Begin(line.From, line.To);
                ShowAngleHint();
            }
            else
            {
                _angleDraft.Clear();
                HideAngleHint();
            }
        }
        else if (phase == AnglePhase.Second && annotation is AngleAnnotation pending)
        {
            if (_angleDraft.TryBuild(pending.B, out _))
            {
                _notes.Add(pending with
                {
                    Label = FormatAngle(pending.Vertex, pending.A, pending.B)
                });
                MarkNoteMutation();
                _angleDraft.Clear();
                HideAngleHint();
            }
            else
            {
                // 第二条边太短：保留草稿，用户可以再拖一次
                ShowAngleHint();
            }
        }

        RebuildNotePreview();
        UpdateToolButtons();
    }

    /// <summary>清掉角度草稿；Esc / 右键 / 切工具共用。</summary>
    private void CancelAngleDraft()
    {
        if (!_angleDraft.IsActive && _anglePhase == AnglePhase.None)
            return;

        _angleDraft.Clear();
        _anglePhase = AnglePhase.None;
        _pendingAnnotation = null;
        _penPoints = null;
        HideAngleHint();
        RebuildNotePreview();
        UpdateToolButtons();
    }

    private static string FormatAngle(Point vertex, Point a, Point b) =>
        $"{AnnotationGeometry.AngleBetween(vertex, a, b):0}°";

    private void ShowAngleHint()
    {
        AngleHint.Measure(Size.Infinity);
        Canvas.SetLeft(AngleHint, Math.Max(8, (Width - AngleHint.DesiredSize.Width) / 2));
        AngleHint.IsVisible = true;
    }

    private void HideAngleHint() => AngleHint.IsVisible = false;

    /// <summary>把已提交的标注 + 正在画的那条重画到预览层。</summary>
    private void RebuildNotePreview()
    {
        NoteLayer.Children.Clear();
        NoteHandleLayer.Children.Clear();
        NoteHandleLayer.IsVisible = false;

        // 撤销 / 删除之后索引可能越界
        if (_selectedNote >= _notes.Items.Count)
            _selectedNote = -1;

        if (!_hasSelection || (_notes.Items.Count == 0 && _pendingAnnotation is null))
        {
            NoteLayer.IsVisible = false;
            return;
        }

        NoteLayer.IsVisible = true;
        NoteLayer.Width = _selection.Width;
        NoteLayer.Height = _selection.Height;
        Canvas.SetLeft(NoteLayer, _selection.X);
        Canvas.SetTop(NoteLayer, _selection.Y);

        foreach (var item in _notes.Items)
            AddPreviewShape(item);

        if (_pendingAnnotation is not null)
            AddPreviewShape(_pendingAnnotation);

        // 选中态：虚线外框，告诉用户现在拖的是哪一条
        if (_selectedNote >= 0 && _selectedNote < _notes.Items.Count)
        {
            var selected = _notes.Items[_selectedNote];
            AddNoteSelectionOutline(
                NoteBounds(selected),
                NoteRotationCenter(selected),
                AnnotationGeometry.RotationOf(selected));
        }

        UpdateNoteHandles();
    }
}
