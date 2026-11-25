// CaptureWindow 分部类：选区创建、移动、缩放、视觉反馈、放大镜、控制点与右键菜单。
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
    private void BeginCreate(Point point)
    {
        _hasSelection = false;
        _mode = DragMode.Create;
        _createStart = point;
        _createConstrained = false;
        _selection = new Rect(point, new Size());
        _selectionOrigin = _selection;
        ClearGuides();

        // 记住新选区落在哪个窗口上：拖动/拉伸时吸它的边和中线
        _snapBox = _candidateAtPress is { } candidate ? ToDip(candidate.Bounds) : null;

        Result = null;
        FallbackText = null;
        ActionBar.IsVisible = false;
        HandleLayer.IsVisible = false;
        OcrStatus.IsVisible = false;
        OcrBoxButton.IsVisible = false;
        _ocrActionsAvailable = false;
        HintBar.IsVisible = false;
        _notes.Clear();
        _pendingAnnotation = null;
        _penPoints = null;
        _notePressOnExisting = false;
        _notesBeforeDrag = null;
        _selectedNote = -1;
        _draggingNote = -1;
        _noteDragOriginal = null;
        _nextNumber = 1;
        _selectionUndo.Clear();
        _selectionRedo.Clear();
        CancelAngleDraft();
        RebuildNotePreview();
        UpdateToolButtons();
        _ocrText = null;

        OcrLayer.Children.Clear();
        OcrLayer.IsVisible = false;

        Selection.IsVisible = true;
        ApplyCreate(point);
        Cursor = CrossCursor;
    }

    private void ApplyCreate(Point current, bool constrain = false)
    {
        _toolbarManual = false;

        var x = Math.Min(_createStart.X, current.X);
        var y = Math.Min(_createStart.Y, current.Y);
        var width = Math.Abs(current.X - _createStart.X);
        var height = Math.Abs(current.Y - _createStart.Y);

        // Shift = 正方形（已锁比例时沿用比例）；否则只有锁定比例才约束
        var aspect = constrain
            ? (_aspectLocked && EffectiveAspect() > 0 ? EffectiveAspect() : 1)
            : (_aspectLocked ? EffectiveAspect() : 0);

        if (aspect > 0)
        {
            var size = constrain ? Math.Max(width, height) : width;
            width = Math.Max(size, MinSelectionSize);
            height = width / aspect;

            var growsLeft = current.X < _createStart.X;
            var growsUp = current.Y < _createStart.Y;
            x = growsLeft ? _createStart.X - width : _createStart.X;
            y = growsUp ? _createStart.Y - height : _createStart.Y;

            // 越界就按可用空间反算，保证比例不变
            if (x < 0)
            {
                width = Math.Max(MinSelectionSize, width + x);
                x = 0;
                height = width / aspect;
                if (growsUp) y = _createStart.Y - height;
            }

            if (y < 0)
            {
                height = Math.Max(MinSelectionSize, height + y);
                y = 0;
                width = height * aspect;
                if (growsLeft) x = _createStart.X - width;
            }

            if (x + width > Width)
            {
                width = Math.Max(MinSelectionSize, Width - x);
                height = width / aspect;
            }

            if (y + height > Height)
            {
                height = Math.Max(MinSelectionSize, Height - y);
                width = height * aspect;
                if (growsLeft) x = Math.Max(0, _createStart.X - width);
            }
        }

        _selection = new Rect(x, y, width, height);
        UpdateSelectionVisuals();
    }

    /// <summary>
    /// 比例锁下的拉边修正：拖哪个轴就驱动哪个轴，按比例推另一个轴。
    /// 边点保持对边和另一轴中线不动；角点钉住对角。
    /// </summary>
    private (double left, double top, double right, double bottom) ApplyAspectToResize(
        double left, double top, double right, double bottom)
    {
        var aspect = EffectiveAspect();
        if (!_aspectLocked || aspect <= 0)
            return (left, top, right, bottom);

        var w = right - left;
        var h = bottom - top;
        if (w <= 0 || h <= 0)
            return (left, top, right, bottom);

        var isCorner = _resizeHandle
            is ResizeHandle.NW or ResizeHandle.NE or ResizeHandle.SW or ResizeHandle.SE;
        var horizontal = _resizeHandle
            is ResizeHandle.E or ResizeHandle.W or ResizeHandle.NW or ResizeHandle.NE
            or ResizeHandle.SW or ResizeHandle.SE;
        var vertical = _resizeHandle
            is ResizeHandle.N or ResizeHandle.S or ResizeHandle.NW or ResizeHandle.NE
            or ResizeHandle.SW or ResizeHandle.SE;

        // 角点：哪个轴的相对变化大就用哪个驱动
        var driveWidth = horizontal && !vertical;
        if (isCorner)
        {
            var dw = Math.Abs(w - _selectionOrigin.Width) / Math.Max(1, _selectionOrigin.Width);
            var dh = Math.Abs(h - _selectionOrigin.Height) / Math.Max(1, _selectionOrigin.Height);
            driveWidth = dw >= dh;
        }

        var centerX = (left + right) / 2;
        var centerY = (top + bottom) / 2;

        if (driveWidth)
            h = w / aspect;
        else
            w = h * aspect;

        if (w < MinSelectionSize)
        {
            w = MinSelectionSize;
            h = w / aspect;
        }

        if (h < MinSelectionSize)
        {
            h = MinSelectionSize;
            w = h * aspect;
        }

        // 钉住不被拖的那一侧
        if (_resizeHandle is ResizeHandle.W or ResizeHandle.NW or ResizeHandle.SW)
            left = right - w;
        else if (_resizeHandle is ResizeHandle.E or ResizeHandle.NE or ResizeHandle.SE)
            right = left + w;
        else
        {
            left = centerX - w / 2;
            right = centerX + w / 2;
        }

        if (_resizeHandle is ResizeHandle.N or ResizeHandle.NW or ResizeHandle.NE)
            top = bottom - h;
        else if (_resizeHandle is ResizeHandle.S or ResizeHandle.SW or ResizeHandle.SE)
            bottom = top + h;
        else
        {
            top = centerY - h / 2;
            bottom = centerY + h / 2;
        }

        // 越界时整体平移回窗口内，保证比例不被破坏
        if (right > Width) { var d = right - Width; left -= d; right -= d; }
        if (bottom > Height) { var d = bottom - Height; top -= d; bottom -= d; }
        if (left < 0) { var d = -left; left += d; right += d; }
        if (top < 0) { var d = -top; top += d; bottom += d; }

        return (left, top, right, bottom);
    }

    /// <summary>鼠标悬停：高亮窗口，并持续细化到完整命中路径。</summary>

    private void NudgeSelection(double dx, double dy)
    {
        if (!_hasSelection)
            return;

        var left = Math.Clamp(_selection.X + dx, 0, Math.Max(0, Width - _selection.Width));
        var top = Math.Clamp(_selection.Y + dy, 0, Math.Max(0, Height - _selection.Height));
        var moveX = left - _selection.X;
        var moveY = top - _selection.Y;

        if (Math.Abs(moveX) < 0.01 && Math.Abs(moveY) < 0.01)
            return;

        _selectionUndo.Push(new SelectionSnapshot(_selection, _notes.Items.ToList()));
        _selectionRedo.Clear();

        _selection = new Rect(left, top, _selection.Width, _selection.Height);
        _selectionOrigin = _selection;
        TranslateNotesCurrent(-moveX, -moveY);

        UpdateSelectionVisuals();
        UpdateHandles();
        UpdateSizeBadge(_selection);
        PositionActionBar(_selection);
        UpdateToolButtons();
    }

    /// <summary>直接平移当前标注（方向键微调用，不需要拖动快照）。</summary>
    private void TranslateNotesCurrent(double dx, double dy)
    {
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
            return;

        _notes.SetItems(_notes.Items.Select(note => TranslateNote(note, dx, dy)).ToList());
        RebuildNotePreview();
    }

    private void ApplyMove(Point current)
    {
        // 选区一动就恢复「工具条跟随」，对齐参考的 resetPositionForSelection 语义
        _toolbarManual = false;

        var dx = current.X - _dragOrigin.X;
        var dy = current.Y - _dragOrigin.Y;

        var left = Math.Clamp(
            _selectionOrigin.X + dx,
            0,
            Math.Max(0, Width - _selectionOrigin.Width));
        var top = Math.Clamp(
            _selectionOrigin.Y + dy,
            0,
            Math.Max(0, Height - _selectionOrigin.Height));

        // 吸附：屏幕边/中线，以及选区所在窗口的边/中线
        var (snapX, guideX) = SnapGuides.SnapEdges(
            left,
            left + _selectionOrigin.Width,
            SnapGuides.Combine(Width, _snapBox, vertical: true, _activeScreen));
        var (snapY, guideY) = SnapGuides.SnapEdges(
            top,
            top + _selectionOrigin.Height,
            SnapGuides.Combine(Height, _snapBox, vertical: false, _activeScreen));

        left = Math.Clamp(left + snapX, 0, Math.Max(0, Width - _selectionOrigin.Width));
        top = Math.Clamp(top + snapY, 0, Math.Max(0, Height - _selectionOrigin.Height));

        ShowGuides(guideX, guideY);

        _selection = new Rect(left, top, _selectionOrigin.Width, _selectionOrigin.Height);
        UpdateSelectionVisuals();
        TranslateNotesForSelectionChange(left - _selectionOrigin.X, top - _selectionOrigin.Y);
    }

    private void ApplyResize(Point current)
    {
        _toolbarManual = false;

        var dx = current.X - _dragOrigin.X;
        var dy = current.Y - _dragOrigin.Y;

        var left = _selectionOrigin.Left;
        var top = _selectionOrigin.Top;
        var right = _selectionOrigin.Right;
        var bottom = _selectionOrigin.Bottom;

        // 拖哪条边就动哪条边，对边保持不动
        if (_resizeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW)
            left = Math.Min(_selectionOrigin.Left + dx, right - MinSelectionSize);
        if (_resizeHandle is ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE)
            right = Math.Max(_selectionOrigin.Right + dx, left + MinSelectionSize);
        if (_resizeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE)
            top = Math.Min(_selectionOrigin.Top + dy, bottom - MinSelectionSize);
        if (_resizeHandle is ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE)
            bottom = Math.Max(_selectionOrigin.Bottom + dy, top + MinSelectionSize);

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(Width, right);
        bottom = Math.Min(Height, bottom);

        // 吸附：只吸正在拖的那条边
        double? guideX = null;
        double? guideY = null;

        if (_resizeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW)
        {
            var (value, guide) = SnapGuides.Snap(left, SnapGuides.Combine(Width, _snapBox, vertical: true, _activeScreen));
            left = value;
            guideX = guide;
        }
        else if (_resizeHandle is ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE)
        {
            var (value, guide) = SnapGuides.Snap(right, SnapGuides.Combine(Width, _snapBox, vertical: true, _activeScreen));
            right = value;
            guideX = guide;
        }

        if (_resizeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE)
        {
            var (value, guide) = SnapGuides.Snap(top, SnapGuides.Combine(Height, _snapBox, vertical: false, _activeScreen));
            top = value;
            guideY = guide;
        }
        else if (_resizeHandle is ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE)
        {
            var (value, guide) = SnapGuides.Snap(bottom, SnapGuides.Combine(Height, _snapBox, vertical: false, _activeScreen));
            bottom = value;
            guideY = guide;
        }

        ShowGuides(guideX, guideY);

        // 比例锁：拖完之后按比例修正另一轴（拖哪条边就驱动哪个轴）
        (left, top, right, bottom) = ApplyAspectToResize(left, top, right, bottom);

        _selection = new Rect(left, top, right - left, bottom - top);
        UpdateSelectionVisuals();
        TranslateNotesForSelectionChange(left - _selectionOrigin.X, top - _selectionOrigin.Y);
    }

    /// <summary>
    /// 选区原点变化时，把标注反向平移，保持它们在屏幕上的位置不变。
    /// 从拖动开始时的快照重算，避免逐帧累积误差。
    /// </summary>
    private void TranslateNotesForSelectionChange(double deltaX, double deltaY)
    {
        if (_notesBeforeDrag is null
            || (Math.Abs(deltaX) < 0.01 && Math.Abs(deltaY) < 0.01))
            return;

        _notes.SetItems(_notesBeforeDrag.Select(
            note => TranslateNote(note, -deltaX, -deltaY)));
        RebuildNotePreview();
    }

    private void UpdateSelectionVisuals()
    {
        var visible = _selection.Width > 0 && _selection.Height > 0;
        var radius = SelectionRadius();

        Selection.IsVisible = visible;
        SelectionHalo.IsVisible = visible;
        Selection.RadiusX = radius;
        Selection.RadiusY = radius;
        SelectionHalo.RadiusX = radius > 0 ? radius + 1 : 0;
        SelectionHalo.RadiusY = radius > 0 ? radius + 1 : 0;

        Canvas.SetLeft(Selection, _selection.X);
        Canvas.SetTop(Selection, _selection.Y);
        Selection.Width = _selection.Width;
        Selection.Height = _selection.Height;

        // 外圈 1px 深色描边：亮背景上也能把选区边界和内容分开
        Canvas.SetLeft(SelectionHalo, _selection.X - 1);
        Canvas.SetTop(SelectionHalo, _selection.Y - 1);
        SelectionHalo.Width = _selection.Width + 2;
        SelectionHalo.Height = _selection.Height + 2;

        UpdateSizeBadge(_selection);
        UpdateDim();

        if (_hasSelection)
        {
            UpdateHandles();
            PositionActionBar(_selection);
        }
    }

    /// <summary>
    /// 遮罩只压暗选区之外：全屏矩形减去选区（或悬停候选）挖洞。
    /// 选区保持原图亮度，这是选区和参考工具最直观的差别。
    /// </summary>
    private void UpdateDim()
    {
        var bounds = new Rect(0, 0, Math.Max(Width, 1), Math.Max(Height, 1));

        Rect? hole = null;

        if (_selection.Width > 0 && _selection.Height > 0)
            hole = _selection;
        else if (CandidateBox.IsVisible && _candidateDip.Width > 0 && _candidateDip.Height > 0)
            hole = _candidateDip;

        if (hole is not { Width: > 0, Height: > 0 })
        {
            DimLayer.Data = new RectangleGeometry(bounds);
            return;
        }

        DimLayer.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(bounds),
            new RectangleGeometry(hole.Value)
            {
                RadiusX = hole is { } h && h == _selection ? SelectionRadius() : 0,
                RadiusY = hole is { } r && r == _selection ? SelectionRadius() : 0
            });
    }

    /// <summary>选区预览半径：不超过短边的一半。</summary>
    private double SelectionRadius() =>
        _cornerRadius <= 0
            ? 0
            : Math.Min(_cornerRadius, Math.Min(_selection.Width, _selection.Height) / 2);

    /// <summary>光标十字线：还没框选时帮助对齐（参考 snow-shot 的实时参考线）。</summary>
    private void UpdateCrosshair(Point point)
    {
        var show = !_hasSelection
            && !OcrLayer.IsVisible
            && !IsPointInActionBar(point)
            && !_isActing;

        CrosshairLayer.IsVisible = show;

        if (!show)
            return;

        CrosshairV.StartPoint = new Point(point.X, 0);
        CrosshairV.EndPoint = new Point(point.X, Math.Max(Height, 1));
        CrosshairH.StartPoint = new Point(0, point.Y);
        CrosshairH.EndPoint = new Point(Math.Max(Width, 1), point.Y);
    }

    /// <summary>
    /// 拖动/拉伸时在光标旁显示 4 倍放大镜。
    /// 直接用冻结帧按比例缩放，不做像素重采样，没有额外内存。
    /// </summary>
    private void UpdateMagnifier(Point point)
    {
        var show = _frozenBitmap is not null
            && _mode != DragMode.None
            && _pendingAnnotation is null
            && !IsPointInActionBar(point);

        if (!show)
        {
            Magnifier.IsVisible = false;
            return;
        }

        Magnifier.IsVisible = true;
        MagnifierImage.Source = _frozenBitmap;
        MagnifierImage.Width = Math.Max(Width, 1);
        MagnifierImage.Height = Math.Max(Height, 1);

        if (MagnifierImage.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = MagnifierZoom;
            scale.ScaleY = MagnifierZoom;
        }

        // 让光标位置正好落在放大镜中心
        Canvas.SetLeft(MagnifierImage, -(point.X * MagnifierZoom) + MagnifierSize / 2);
        Canvas.SetTop(MagnifierImage, -(point.Y * MagnifierZoom) + MagnifierSize / 2);

        // 依次尝试右下 / 左下 / 右上 / 左上，避开工具条和窗口边缘
        var toolbarLeft = Canvas.GetLeft(ActionBar);
        var toolbarTop = Canvas.GetTop(ActionBar);
        var hasToolbar = ActionBar.IsVisible
            && !double.IsNaN(toolbarLeft)
            && !double.IsNaN(toolbarTop)
            && ActionBar.Bounds.Width > 0;

        var toolbar = hasToolbar
            ? new Rect(
                toolbarLeft,
                toolbarTop,
                ActionBar.Bounds.Width,
                ActionBar.Bounds.Height).Inflate(8)
            : default;

        var placed = PlaceMagnifier(
            point,
            MagnifierSize,
            new Size(Width, Height),
            hasToolbar ? toolbar : null);

        Canvas.SetLeft(Magnifier, placed.X);
        Canvas.SetTop(Magnifier, placed.Y);
    }

    /// <summary>
    /// 放大镜位置：优先右下，贴边或压到工具条就换到另一侧；都放不下就钳制在窗口内。
    /// 抽成纯函数，方便验证「不压工具条」这条规则。
    /// </summary>
    internal static Rect PlaceMagnifier(Point cursor, double size, Size bounds, Rect? avoid, double gap = 28)
    {
        var offsets = new[]
        {
            new Point(gap, gap),
            new Point(-size - gap, gap),
            new Point(gap, -size - gap),
            new Point(-size - gap, -size - gap)
        };

        foreach (var offset in offsets)
        {
            var rect = new Rect(cursor.X + offset.X, cursor.Y + offset.Y, size, size);

            if (rect.X < 8 || rect.Y < 8 || rect.Right > bounds.Width - 8 || rect.Bottom > bounds.Height - 8)
                continue;

            if (avoid is { Width: > 0 } && rect.Intersects(avoid.Value))
                continue;

            return rect;
        }

        var left = Math.Clamp(
            cursor.X + gap,
            8,
            Math.Max(8, bounds.Width - size - 8));
        var top = Math.Clamp(
            cursor.Y + gap,
            8,
            Math.Max(8, bounds.Height - size - 8));

        return new Rect(left, top, size, size);
    }

    private void UpdateHandles()
    {
        HandleLayer.IsVisible = _hasSelection && !OcrLayer.IsVisible;

        if (!HandleLayer.IsVisible)
            return;

        foreach (var (handle, dot) in _handles)
        {
            dot.IsVisible = true;
            Canvas.SetLeft(dot, HandleX(handle) - dot.Width / 2);
            Canvas.SetTop(dot, HandleY(handle) - dot.Height / 2);
        }
    }

    private double HandleX(ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW => _selection.Left,
        ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE => _selection.Right,
        _ => _selection.Center.X
    };

    private double HandleY(ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE => _selection.Top,
        ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE => _selection.Bottom,
        _ => _selection.Center.Y
    };

    private ResizeHandle HitHandle(Point point)
    {
        const double radius = 7;
        var rect = _selection;

        var nearLeft = Math.Abs(point.X - rect.Left) <= radius;
        var nearRight = Math.Abs(point.X - rect.Right) <= radius;
        var nearTop = Math.Abs(point.Y - rect.Top) <= radius;
        var nearBottom = Math.Abs(point.Y - rect.Bottom) <= radius;
        var withinX = point.X >= rect.Left - radius && point.X <= rect.Right + radius;
        var withinY = point.Y >= rect.Top - radius && point.Y <= rect.Bottom + radius;

        if (nearLeft && nearTop)
            return ResizeHandle.NW;
        if (nearRight && nearTop)
            return ResizeHandle.NE;
        if (nearLeft && nearBottom)
            return ResizeHandle.SW;
        if (nearRight && nearBottom)
            return ResizeHandle.SE;
        if (nearLeft && withinY)
            return ResizeHandle.W;
        if (nearRight && withinY)
            return ResizeHandle.E;
        if (nearTop && withinX)
            return ResizeHandle.N;
        if (nearBottom && withinX)
            return ResizeHandle.S;

        return ResizeHandle.None;
    }

    private void UpdateCursor(Point point)
    {
        // 悬停在工具条 / 尺寸浮条 / 弹出层上：始终显示普通箭头
        if (IsPointInActionBar(point)
            || IsPointInControl(SizeBadge, point)
            || IsPointInControl(CustomColorPanel, point)
            || IsPointInControl(ToolGroupPopover, point))
        {
            Cursor = ArrowCursor;
            return;
        }

        if (!_hasSelection || OcrLayer.IsVisible)
        {
            Cursor = CrossCursor;
            return;
        }

        // 选中标注的旋转/缩放点优先于选区移动
        if (_tool == AnnotationTool.None)
        {
            if (HitNoteRotateHandle(point))
            {
                Cursor = HandCursor;
                return;
            }

            var noteHandle = HitNoteHandle(point);
            if (noteHandle != ResizeHandle.None)
            {
                Cursor = ResizeCursorFor(noteHandle);
                return;
            }
        }

        // 边框优先：即使正在用绘图工具，按在控制点上也是拉伸
        var handle = HitHandle(point);
        if (handle != ResizeHandle.None)
        {
            Cursor = ResizeCursorFor(handle);
            return;
        }

        // 绘图工具：选区内显示工具光标，不再伪装成「移动选区」
        if (_tool != AnnotationTool.None)
        {
            if (!_selection.Contains(point))
            {
                Cursor = CrossCursor;
                return;
            }

            Cursor = _tool == AnnotationTool.Text
                ? IbeamCursor
                : CrossCursor;
            return;
        }

        Cursor = _selection.Contains(point) ? MoveCursor : CrossCursor;
    }

    private static Cursor ResizeCursorFor(ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW or ResizeHandle.SE => NwseCursor,
        ResizeHandle.NE or ResizeHandle.SW => NeswCursor,
        ResizeHandle.N or ResizeHandle.S => NorthSouthCursor,
        ResizeHandle.E or ResizeHandle.W => WestEastCursor,
        _ => ArrowCursor
    };

    private bool IsPointInControl(Control control, Point point)
    {
        if (!control.IsVisible)
            return false;

        // 刚显示、还没跑过布局时 Bounds 是空的，用 DesiredSize 兜底
        var size = control.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            size = control.DesiredSize;

        return control.TranslatePoint(new Point(0, 0), this) is { } origin
               && new Rect(origin, size).Contains(point);
    }

    /// <summary>
    /// Esc / 右键用于取消“进行中的操作”：拖选区、拖标注、画一半、橡皮擦、拖工具条。
    /// 返回 true 表示这次按键/右键已经被取消动作消费。
    /// </summary>
    private bool CancelActiveDrag()
    {
        var active = _pendingAnnotation is not null
                     || _mode != DragMode.None
                     || _erasing
                     || _draggingToolbar;

        if (!active)
            return false;

        _draggingToolbar = false;
        _erasing = false;

        if (_mode == DragMode.Create)
        {
            _mode = DragMode.None;
            _pendingAnnotation = null;
            _penPoints = null;
            ResetSelection();
            return true;
        }

        if (_mode is DragMode.Move or DragMode.Resize)
        {
            _selection = _selectionOrigin;
            if (_notesBeforeDrag is { } notes)
                _notes.SetItems(notes);
        }
        else if (_mode == DragMode.NoteEdit
                 && _draggingNote >= 0
                 && _noteDragOriginal is not null)
        {
            _notes.Replace(_draggingNote, _noteDragOriginal);
        }
        else if (_mode == DragMode.NoteResize
                 && _noteResizeIndex >= 0
                 && _noteResizeOriginal is { } resizeOriginal)
        {
            _notes.Replace(_noteResizeIndex, resizeOriginal);
        }
        else if (_mode == DragMode.NoteRotate
                 && _noteRotateIndex >= 0
                 && _noteRotateOriginal is { } rotateOriginal)
        {
            _notes.Replace(_noteRotateIndex, rotateOriginal);
        }

        _mode = DragMode.None;
        _draggingNote = -1;
        _noteDragOriginal = null;
        _notesBeforeDrag = null;
        _pendingAnnotation = null;
        _penPoints = null;
        _noteDragMoved = false;
        _noteResizeIndex = -1;
        _noteResizeOriginal = null;
        _noteResizeMoved = false;
        _noteRotateIndex = -1;
        _noteRotateOriginal = null;
        _noteRotateMoved = false;
        HideTextEditor();
        Magnifier.IsVisible = false;
        ClearGuides();

        if (_hasSelection)
        {
            UpdateSelectionVisuals();
            UpdateHandles();
            UpdateSizeBadge(_selection);
            PositionActionBar(_selection);
        }

        RebuildNotePreview();
        UpdateToolButtons();
        return true;
    }

    /// <summary>
    /// 右键逐级后退，和参考工具的肌肉记忆一致：
    /// 拾色器 / 标注工具 → 指针；有选区 → 清空重选；什么都没有 → 取消截图。
    /// </summary>
    private void HandleRightClick()
    {
        // 角度草稿：先清草稿，不退出工具（再按一次右键才退）
        if (_angleDraft.IsActive || _anglePhase != AnglePhase.None)
        {
            CancelAngleDraft();
            return;
        }

        // 取色是最外层：先退出取色，再谈别的
        if (_colorSampling)
        {
            ExitColorSampling();
            return;
        }

        if (CustomColorPanel.IsVisible)
        {
            CustomColorPanel.IsVisible = false;
            return;
        }

        // 正在拖选区/拖标注/画一半：右键先取消这次操作
        if (CancelActiveDrag())
            return;

        if (_tool != AnnotationTool.None)
        {
            _tool = AnnotationTool.None;
            UpdateToolButtons();
            return;
        }

        if (OcrLayer.IsVisible)
        {
            ClearOcrOverlay();
            return;
        }

        if (_hasSelection)
        {
            ResetSelection();
            return;
        }

        Action = CaptureAction.None;
        Close();
    }

    private void ResetSelection()
    {
        _hasSelection = false;
        _selectedNote = -1;
        _draggingNote = -1;
        _noteDragOriginal = null;
        _selection = default;
        Selection.IsVisible = false;
        SelectionHalo.IsVisible = false;
        Magnifier.IsVisible = false;
        HideCandidate();
        HandleLayer.IsVisible = false;
        ActionBar.IsVisible = false;
        SizeBadge.IsVisible = false;
        HideToolGroupPopover();
        HintBar.IsVisible = true;
        _notes.Clear();
        _nextNumber = 1;
        _pendingAnnotation = null;
        _penPoints = null;
        _notesBeforeDrag = null;
        _selectionUndo.Clear();
        _selectionRedo.Clear();
        RebuildNotePreview();
        UpdateToolButtons();
        UpdateDim();
        Cursor = CrossCursor;
    }
}
