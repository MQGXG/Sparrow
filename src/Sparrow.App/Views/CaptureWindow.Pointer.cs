// CaptureWindow 分部类：指针按下/移动/释放的主流程。
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
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (_isActing || _isRecognizing)
            return;

        var point = e.GetPosition(this);

        // 录制中锁定选区：只允许在选区内用标注工具画，不能拖拽/拉伸/移动。
        if (_recording is not null && !CanAnnotateDuringRecording(point))
            return;

        // 右键逐级退出（参考 snow-shot / LumaShot 的手感）
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            HandleRightClick();
            e.Handled = true;
            return;
        }

        // 自定义颜色面板：点里面交给控件，点外面先收起
        if (CustomColorPanel.IsVisible)
        {
            if (IsPointInCustomColorPanel(point))
                return;

            CustomColorPanel.IsVisible = false;
        }

        // 点在动作栏上：交给按钮，不要当成新选区
        if (IsPointInActionBar(point))
            return;

        // 点在尺寸角标上：切换单位，不要当成新选区
        if (SizeBadge.IsVisible
            && SizeBadge.TranslatePoint(new Point(0, 0), this) is { } badgeOrigin
            && new Rect(badgeOrigin, SizeBadge.Bounds.Size).Contains(point))
        {
            return;
        }

        // 点空白处收起工具组选项条；点选项条本身要放过去，否则按钮在按下时就没了
        if (ToolGroupPopover.IsVisible)
        {
            if (_toolGroupPopoverRect.Contains(point))
            {
                return;
            }

            HideToolGroupPopover();
        }

        // 取色器是常驻预览，不拦截点击；点击画布仍然走选区 / 标注操作

        // 角度：两次拖拽；草稿存在时第二段优先于边框拉伸
        if (_tool == AnnotationTool.Angle && _hasSelection && _selection.Contains(point))
        {
            BeginAngleDrag(point);
            e.Handled = true;
            return;
        }

        // 边框带优先于当前工具：画着标注时按在边框 8 内也能拉选区（参考 snow-shot），
        // 松手后把工具恢复回去；录制中选区锁定，不走这条。
        _toolBeforeResize = null;
        if (_recording is null && _tool != AnnotationTool.None && _hasSelection
            && HitHandle(point) != ResizeHandle.None)
        {
            _toolBeforeResize = _tool;
            ForceTool(AnnotationTool.None);
        }

        // 水印 / 气泡：按在已有同类标注上先不新建（双击改文字，单击只选中）
        _notePressOnExisting = false;
        if (_tool is AnnotationTool.Watermark or AnnotationTool.Bubble && _hasSelection)
        {
            var hit = HitNote(point);
            var sameKind = hit >= 0 && _notes.Items[hit] switch
            {
                WatermarkAnnotation when _tool == AnnotationTool.Watermark => true,
                SpeechBubbleAnnotation when _tool == AnnotationTool.Bubble => true,
                _ => false
            };

            if (sameKind)
            {
                if (e.ClickCount == 2)
                    EditNoteText(hit);
                else
                    SelectNote(hit);

                _notePressOnExisting = e.ClickCount != 2;
                e.Handled = true;
                return;
            }
        }

        // 选了标注工具：在选区内按下就是开始画，不移动选区
        if (BeginAnnotation(point))
        {
            e.Handled = true;
            return;
        }

        // 橡皮擦：按下擦掉指针下最上面的一条，拖动连续擦
        if (_tool == AnnotationTool.Eraser)
        {
            if (_hasSelection && _selection.Contains(point))
            {
                _erasing = true;
                EraseAt(point);
            }

            e.Handled = true;
            return;
        }

        // 指针工具：先看点没点中已有标注（点中就拖动它），否则继续移动选区
        if (_tool == AnnotationTool.None && _hasSelection && _selection.Contains(point))
        {
            if (HitNoteRotateHandle(point))
            {
                BeginNoteRotate(point);
                e.Handled = true;
                return;
            }

            // 序号组合的目标点把手优先于普通标注拖动
            if (HitNumberTargetHandle(point))
            {
                _mode = DragMode.NumberTarget;
                _draggingNote = _selectedNote;
                _noteDragOriginal = _notes.Items[_selectedNote];
                _noteDragMoved = false;
                e.Handled = true;
                return;
            }

            var noteHandle = HitNoteHandle(point);
            if (noteHandle != ResizeHandle.None)
            {
                BeginNoteResize(point, noteHandle);
                e.Handled = true;
                return;
            }

            var hit = HitNote(point);
            if (hit >= 0)
            {
                // 双击序号说明：就地编辑文字
                if (e.ClickCount == 2
                    && _notes.Items[hit] is NumberAnnotation { Combo: NumberCombo.Text }
                    && EditNoteText(hit))
                {
                    SelectNote(hit);
                    e.Handled = true;
                    return;
                }

                SelectNote(hit);
                _draggingNote = hit;
                _noteDragOriginal = _notes.Items[hit];
                _noteDragStart = new Point(point.X - _selection.X, point.Y - _selection.Y);
                _noteDragMoved = false;
                _mode = DragMode.NoteEdit;
                Cursor = MoveCursor;
                e.Handled = true;
                return;
            }

            SelectNote(-1);   // 点空白：先取消选中，再走原来的移动/拉伸
        }

        // 文字标记菜单：点它只是操作菜单，不能当成"点空白清空识别"
        if (IsPointInControl(OcrMarkMenu, point))
            return;

        // 识别结果常驻、可继续选择复制：点空白只取消选择，不退出识别；
        // 退出识别用 Esc / 右键，彻底结束用取消截图
        if (OcrLayer.IsVisible)
        {
            if (e.Source is not Rectangle)
                ClearOcrSelection();

            e.Handled = true;
            return;
        }

        if (_hasSelection)
        {
            // 纯标注模式：图片就是选区，只允许整体平移（底图跟着一起动）。
            // 缩放手柄、吸附线、「框外重新框选」都会改选区尺寸或位置，
            // 让居中的图片与选区脱节，这里一律挡掉。
            if (AnnotationOnly)
            {
                if (_selection.Contains(point))
                {
                    _mode = DragMode.Move;
                    _dragOrigin = point;
                    _selectionOrigin = _selection;
                    HideTextEditor();
                    Cursor = MoveCursor;
                }

                e.Handled = true;
                return;
            }

            var handle = HitHandle(point);
            if (handle != ResizeHandle.None)
            {
                _mode = DragMode.Resize;
                _resizeHandle = handle;
                _dragOrigin = point;
                _selectionOrigin = _selection;
                _activeScreen = ActiveScreenDip();
                _notesBeforeDrag = _notes.Items.ToList();
                HideTextEditor();
                e.Handled = true;
                return;
            }

            if (_selection.Contains(point))
            {
                _mode = DragMode.Move;
                _dragOrigin = point;
                _selectionOrigin = _selection;
                _activeScreen = ActiveScreenDip();
                _notesBeforeDrag = _notes.Items.ToList();
                HideTextEditor();
                Cursor = MoveCursor;
                e.Handled = true;
                return;
            }
        }

        // 框外按下：重新拉一个
        // 任何按下都先结束候选态：拖动就是自由选区，单击才用当前路径层
        _candidateAtPress = _currentCandidate;
        _uiCandidateAtPress = _elementSelection.Current;
        HideCandidate();

        BeginCreate(point);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        _lastPointerPoint = point;

        // 录制中只处理标注 / 橡皮擦 / 工具条拖动 / 工具条悬停；
        // 选区拖拽和拉伸保持锁定，选区外的悬停逻辑也不需要跑。
        if (_recording is not null
            && _pendingAnnotation is null
            && !_erasing
            && !_draggingToolbar
            && !IsPointInActionBar(point))
            return;

        // 工具组选项条：指针在「触发按钮 + 选项条」并集里就取消收起；
        // 离开只安排一次延迟复核，到点再按当前光标判断（对齐 snow-shot 的 finishHoverClose）。
        if (ToolGroupPopover.IsVisible && _activeGroup is not null)
        {
            var hoverArea = _toolGroupPopoverRect.Union(_toolGroupTriggerRect).Inflate(10);

            if (hoverArea.Contains(point))
                _groupCloseTimer?.Stop();
            else
                ScheduleCloseToolGroupPopover();
        }

        // 更多识别选项条：指针离开「按钮 + 选项条」并集才收起。
        // 两者之间有 6px 间隙，逐像素判断会让选项条在鼠标移过去之前就消失。
        if (RecognitionPopover.IsVisible)
        {
            var hover = RectOfControl(RecognitionPopover)
                .Union(RectOfControl(MoreRecognitionButton))
                .Inflate(10);

            if (!hover.Contains(point))
                HideRecognitionPopover();
        }

        // 橡皮擦拖动：连续擦掉指针下的标注
        if (_erasing)
        {
            EraseAt(point);
            e.Handled = true;
            return;
        }

        // 拖动工具条：优先于选区/标注
        if (_draggingToolbar)
        {
            MoveToolbar(point);
            Magnifier.IsVisible = false;
            CrosshairLayer.IsVisible = false;
            e.Handled = true;
            return;
        }

        // 正在画标注：更新预览，不参与移动/拉伸选区
        if (_pendingAnnotation is not null)
        {
            var target = point;

            // 测距按住 Shift：方向吸附到 45° 的整数倍
            if (_pendingAnnotation is DistanceAnnotation &&
                e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                var origin = new Point(
                    _selection.X + _annotationStart.X,
                    _selection.Y + _annotationStart.Y);
                target = AnnotationGeometry.SnapToAngle(origin, point);
            }

            UpdatePendingAnnotation(target);
            RebuildNotePreview();
            Magnifier.IsVisible = false;
            CrosshairLayer.IsVisible = false;
            e.Handled = true;
            return;
        }

        // 没有选区、没有 OCR overlay 时，悬停高亮光标下的窗口
        if (_mode == DragMode.None && !_hasSelection && !OcrLayer.IsVisible)
        {
            UpdateCandidateAt(point);
        }
        else
        {
            HideCandidate();
        }

        UpdateAutoColorProbe(point);
        UpdateCrosshair(point);

        switch (_mode)
        {
            case DragMode.Create:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _createConstrained = true;

                ApplyCreate(point, _createConstrained);
                UpdateMagnifier(point);
                e.Handled = true;
                return;
            case DragMode.Move:
                ApplyMove(point);
                if (!AnnotationOnly)
                    UpdateMagnifier(point);
                e.Handled = true;
                return;
            case DragMode.Resize:
                ApplyResize(point);
                UpdateMagnifier(point);
                e.Handled = true;
                return;
            case DragMode.NoteEdit:
                ApplyNoteDrag(point);
                Magnifier.IsVisible = false;
                e.Handled = true;
                return;
            case DragMode.NoteResize:
                ApplyNoteResize(point);
                Magnifier.IsVisible = false;
                e.Handled = true;
                return;
            case DragMode.NoteRotate:
                ApplyNoteRotate(point, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                Magnifier.IsVisible = false;
                e.Handled = true;
                return;
            case DragMode.NumberTarget:
                ApplyNumberTarget(point);
                Magnifier.IsVisible = false;
                e.Handled = true;
                return;
        }

        Magnifier.IsVisible = false;
        UpdateCursor(point);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_draggingToolbar)
        {
            _draggingToolbar = false;
            if (_hasSelection)
                RememberToolbarPlacement();

            e.Handled = true;
            return;
        }

        if (_erasing)
        {
            _erasing = false;
            e.Handled = true;
            return;
        }

        // 按在已有水印上松手：本次不新建
        if (_notePressOnExisting)
        {
            _notePressOnExisting = false;
            e.Handled = true;
            return;
        }

        // 画完一条标注：小于阈值当误触丢掉
        if (_pendingAnnotation is not null)
        {
            if (_tool == AnnotationTool.Angle)
                CommitAngleDrag();
            else
                CommitPendingAnnotation();

            ClearGuides();
            e.Handled = true;
            return;
        }

        ClearGuides();

        // 拖动编辑结束：标注已经落在新位置，保持选中方便继续微调 / 删除
        if (_mode == DragMode.NoteEdit)
        {
            if (_noteDragMoved
                && _draggingNote >= 0
                && _noteDragOriginal is { } original
                && _draggingNote < _notes.Items.Count)
            {
                _notes.RecordReplace(_draggingNote, original, _notes.Items[_draggingNote]);
            }

            _mode = DragMode.None;
            _draggingNote = -1;
            _noteDragOriginal = null;
            _noteDragMoved = false;
            RebuildNotePreview();
            e.Handled = true;
            return;
        }

        // 序号组合的目标点拖完：整条标注已经跟着重新推导
        if (_mode == DragMode.NumberTarget)
        {
            if (_noteDragMoved
                && _draggingNote >= 0
                && _noteDragOriginal is { } targetOriginal
                && _draggingNote < _notes.Items.Count)
            {
                _notes.RecordReplace(_draggingNote, targetOriginal, _notes.Items[_draggingNote]);
            }

            _mode = DragMode.None;
            _draggingNote = -1;
            _noteDragOriginal = null;
            _noteDragMoved = false;
            RebuildNotePreview();
            UpdateNoteHandles();
            e.Handled = true;
            return;
        }

        if (_mode == DragMode.NoteResize)
        {
            if (_noteResizeMoved
                && _noteResizeIndex >= 0
                && _noteResizeOriginal is { } original
                && _noteResizeIndex < _notes.Items.Count)
            {
                _notes.RecordReplace(_noteResizeIndex, original, _notes.Items[_noteResizeIndex]);
            }

            _mode = DragMode.None;
            _noteResizeIndex = -1;
            _noteResizeOriginal = null;
            _noteResizeMoved = false;
            RebuildNotePreview();
            e.Handled = true;
            return;
        }

        if (_mode == DragMode.NoteRotate)
        {
            if (_noteRotateMoved
                && _noteRotateIndex >= 0
                && _noteRotateOriginal is { } original
                && _noteRotateIndex < _notes.Items.Count)
            {
                _notes.RecordReplace(_noteRotateIndex, original, _notes.Items[_noteRotateIndex]);
            }

            _mode = DragMode.None;
            _noteRotateIndex = -1;
            _noteRotateOriginal = null;
            _noteRotateMoved = false;
            RebuildNotePreview();
            e.Handled = true;
            return;
        }

        if (_mode == DragMode.None)
            return;

        var mode = _mode;
        _mode = DragMode.None;
        Magnifier.IsVisible = false;

        var candidateAtPress = _candidateAtPress;
        var uiCandidateAtPress = _uiCandidateAtPress;
        _candidateAtPress = null;
        _uiCandidateAtPress = null;

        if (mode == DragMode.Create)
        {
            ApplyCreate(
                e.GetPosition(this),
                _createConstrained || e.KeyModifiers.HasFlag(KeyModifiers.Shift));

            if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            {
                // 没拖动就是一次单击：有控件候选选中控件，否则选中窗口
                if (candidateAtPress is not null)
                {
                    ApplyCandidate(candidateAtPress, uiCandidateAtPress);
                    return;
                }

                ResetSelection();
                return;
            }

            _hasSelection = true;
            ShowActionBar(_selection);
            UpdateHandles();
            UpdateSizeBadge(_selection);
            Cursor = MoveCursor;

            // 选区已经定下来了，候选框和 UIA 树都不会再用：现在就把上千个
            // COM 节点还掉，别一直压到截图窗口关闭
            _uiProbe?.ReleaseTrees();
        }
        else
        {
            // 纯标注模式：整体平移结束不刷新选区尺寸/手柄，工具条也不跟随
            // 选区（用户可自由拖动），否则刚隐藏的选区 UI 会又被显示出来。
            if (!AnnotationOnly)
            {
                // 移动/拉伸结束：动作栏跟着新位置走
                UpdateSizeBadge(_selection);
                PositionActionBar(_selection);
                UpdateHandles();
            }

            // 边框临时让位的绘图工具在这里恢复
            if (_toolBeforeResize is { } previous)
            {
                _toolBeforeResize = null;
                ForceTool(previous);
            }

            // 几何真的变了才进撤销栈；快照是拖动开始前的选区 + 标注。
            // 标注模式不压栈：选区就是图片矩形，撤销不该把它挪回去。
            if (!AnnotationOnly
                && _notesBeforeDrag is { } notesBefore
                && _selection != _selectionOrigin)
            {
                _selectionUndo.Push(new SelectionSnapshot(_selectionOrigin, notesBefore));
                _selectionRedo.Clear();
                UpdateToolButtons();
            }
        }

        _notesBeforeDrag = null;
        e.Handled = true;
    }
}
