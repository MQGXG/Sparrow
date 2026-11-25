// CaptureWindow 分部类：结果合成、尺寸/比例/预设、参考线与工具条定位停靠。
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
    /// <summary>导出前的合成：先画标注，再按需切圆角。译文视图下以译文图为底。</summary>
    private byte[] ComposeResult(byte[] png)
    {
        var basePng = _translatedPng is not null && !_showOriginalView ? _translatedPng : png;
        var composed = AnnotationRenderer.Compose(basePng, _notes.Items, _scaling, _ocrMarks);
        return AnnotationRenderer.RoundCorners(composed, _cornerRadius * _scaling);
    }

    private void OnSizeUnitClick(object? sender, RoutedEventArgs e) => ToggleSizeUnit();

    private void OnSizeLockClick(object? sender, RoutedEventArgs e) =>
        SetAspectLock(SizeLockButton.IsChecked == true);

    private void OnSizeBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSizeEdit(AxisFor(sender));
            Focus();   // 交还焦点，数字键继续用来切工具
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // 放弃这次输入，恢复成当前选区的尺寸
            UpdateSizeBadge(_selection);
            Focus();
            e.Handled = true;
        }
    }

    private void OnSizeBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // 点单位 / 锁按钮时先不提交：否则会按旧单位先换算一遍再切换
        var focused = FocusManager?.GetFocusedElement();
        if (ReferenceEquals(focused, SizeUnitButton) || ReferenceEquals(focused, SizeLockButton))
            return;

        CommitSizeEdit(AxisFor(sender));
    }

    /// <summary>输入框 → 提交轴：0 宽 / 1 高 / 2 X / 3 Y / 4 圆角。</summary>
    private int AxisFor(object? sender)
    {
        if (ReferenceEquals(sender, PosXBox)) return 2;
        if (ReferenceEquals(sender, PosYBox)) return 3;
        if (ReferenceEquals(sender, RadiusBox)) return 4;
        if (ReferenceEquals(sender, HeightBox)) return 1;
        return 0;
    }

    /// <summary>当前生效的宽高比：优先用预设，否则用选区自身的比例。</summary>
    private double EffectiveAspect() =>
        _ratioPreset > 0
            ? _ratioPreset
            : _selection.Height > 0 ? _selection.Width / _selection.Height : 0;

    /// <summary>把输入框里的数值应用到选区：左上角不动，超出窗口就钳制。</summary>
    internal void CommitSizeEdit(int axis)
    {
        if (_sizeEditGuard || !_hasSelection)
            return;

        // 圆角不是几何，单独处理
        if (axis == 4)
        {
            if (int.TryParse(RadiusBox.Text?.Trim(), out var radius) && radius >= 0)
            {
                _cornerRadius = Math.Min(radius, 128);
                if (_hasSelection)
                    UpdateSelectionVisuals();
                SetSelected(CornerRadiusButton, _cornerRadius > 0);
            }
            UpdateSizeBadge(_selection);
            return;
        }

        var unit = _sizeInPixels ? 1 / _scaling : 1;

        // X / Y：整体平移，尺寸不变
        if (axis is 2 or 3)
        {
            var text = axis == 2 ? PosXBox.Text : PosYBox.Text;
            if (!TryParsePosition(text, out var value))
            {
                UpdateSizeBadge(_selection);
                return;
            }

            var pos = value * unit;
            var x = axis == 2
                ? Math.Clamp(pos, 0, Math.Max(0, Width - _selection.Width))
                : _selection.X;
            var y = axis == 3
                ? Math.Clamp(pos, 0, Math.Max(0, Height - _selection.Height))
                : _selection.Y;
            _selection = new Rect(x, y, _selection.Width, _selection.Height);
            UpdateSelectionVisuals();
            RebuildNotePreview();
            return;
        }

        if (!TryParseSize(WidthBox.Text, out var width) || !TryParseSize(HeightBox.Text, out var height))
        {
            UpdateSizeBadge(_selection);
            return;
        }

        // 输入单位跟当前显示单位一致
        var w = width * unit;
        var h = height * unit;

        // 锁定比例：以最后编辑的那个框为准
        var aspect = EffectiveAspect();
        if (_aspectLocked && aspect > 0)
        {
            if (axis == 0)
                h = w / aspect;
            else
                w = h * aspect;
        }

        // 最小 8 DIP，且不能越过窗口右下边
        w = Math.Clamp(w, 8, Math.Max(8, Width - _selection.X));
        h = Math.Clamp(h, 8, Math.Max(8, Height - _selection.Y));

        _selection = new Rect(_selection.X, _selection.Y, w, h);
        UpdateSelectionVisuals();
        RebuildNotePreview();
    }

    private static bool TryParseSize(string? text, out double value) =>
        double.TryParse(
            text?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) && value > 0;

    private static bool TryParsePosition(string? text, out double value) =>
        double.TryParse(
            text?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) && value >= 0;

    // ---------- 宽高比预设 ----------

    private void OnRatioFreeMenu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(0, "自由");
    private void OnRatio1x1Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(1.0, "1:1");
    private void OnRatio3x2Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(3.0 / 2.0, "3:2");
    private void OnRatio4x3Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(4.0 / 3.0, "4:3");
    private void OnRatio16x9Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(16.0 / 9.0, "16:9");
    private void OnRatio2x3Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(2.0 / 3.0, "2:3");
    private void OnRatio3x4Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(3.0 / 4.0, "3:4");
    private void OnRatio9x16Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(9.0 / 16.0, "9:16");

    /// <summary>
    /// 应用比例预设：ratio = 0 表示自由。
    /// 选预设会顺带打开比例锁（和参考的行为一致），并把当前选区改成该比例。
    /// </summary>
    internal void ApplyRatioPreset(double ratio, string label)
    {
        _ratioPreset = ratio;
        RatioButton.Content = label;

        if (ratio <= 0)
        {
            UpdateSizeBadge(_selection);
            return;
        }

        SetAspectLock(true);

        if (!_hasSelection)
            return;

        // 以当前宽度为准重算高度；越界就退回高度反算宽度
        var w = _selection.Width;
        var h = w / ratio;
        if (_selection.Y + h > Height)
        {
            h = Math.Max(8, Height - _selection.Y);
            w = h * ratio;
        }

        w = Math.Max(8, Math.Min(w, Width - _selection.X));
        _selection = new Rect(_selection.X, _selection.Y, w, h);
        UpdateSelectionVisuals();
        RebuildNotePreview();
    }

    /// <summary>开关比例锁（菜单和锁图标共用）。</summary>
    private void SetAspectLock(bool locked)
    {
        _aspectLocked = locked;
        SizeLockButton.IsChecked = locked;
        LockOpenIcon.IsVisible = !locked;
        LockClosedIcon.IsVisible = locked;
        ToolTip.SetTip(SizeLockButton, locked ? "已锁定宽高比" : "锁定宽高比");
    }

    internal void ToggleSizeUnit()
    {
        _sizeInPixels = !_sizeInPixels;
        UpdateSizeBadge(_selection);
    }

    // ---------- 样式模板（三槽） ----------

    /// <summary>三槽样式模板；由宿主在窗口显示前灌入，关闭后读回持久化。</summary>
    public AnnotationPreset?[] Presets { get; } = new AnnotationPreset?[3];

    /// <summary>当前标注（测试和导出共用，顺序就是叠放顺序）。</summary>
    internal IReadOnlyList<IAnnotation> Notes => _notes.Items;

    internal void SavePreset(int index)
    {
        if (index < 0 || index >= Presets.Length)
            return;

        Presets[index] = new AnnotationPreset(
            $"槽 {index + 1}",
            $"#{_noteColor.R:X2}{_noteColor.G:X2}{_noteColor.B:X2}",
            _noteWidth,
            _mosaicBlur,
            _cornerRadius);

        BuildPresetMenu();
    }

    internal void ApplyPreset(int index)
    {
        if (index < 0 || index >= Presets.Length || Presets[index] is not { } preset)
            return;

        _noteColor = Color.Parse(preset.Color);
        _noteWidth = preset.Width;
        _mosaicBlur = preset.MosaicBlur;
        _cornerRadius = preset.CornerRadius;
        UpdateStyleButtons();

        if (_hasSelection)
            UpdateSelectionVisuals();
    }

    internal void ClearPreset(int index)
    {
        if (index < 0 || index >= Presets.Length)
            return;

        Presets[index] = null;
        BuildPresetMenu();
    }

    /// <summary>「模板」菜单按当前三槽重建：有内容的槽多出「应用 / 清除」。</summary>
    internal void BuildPresetMenu()
    {
        if (PresetButton.Flyout is not MenuFlyout menu)
            return;

        menu.Items.Clear();

        for (var index = 0; index < Presets.Length; index++)
        {
            if (Presets[index] is not { } preset)
                continue;

            var slot = index;
            var apply = new MenuItem { Header = $"应用 {preset.Name}" };
            apply.Click += (_, _) => ApplyPreset(slot);
            menu.Items.Add(apply);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());

        for (var index = 0; index < Presets.Length; index++)
        {
            var slot = index;
            var save = new MenuItem
            {
                Header = Presets[index] is null ? $"保存到槽 {index + 1}" : $"覆盖槽 {index + 1}"
            };
            save.Click += (_, _) => SavePreset(slot);
            menu.Items.Add(save);
        }

        for (var index = 0; index < Presets.Length; index++)
        {
            if (Presets[index] is null)
                continue;

            var slot = index;
            var clear = new MenuItem { Header = $"清除槽 {index + 1}" };
            clear.Click += (_, _) => ClearPreset(slot);
            menu.Items.Add(clear);
        }
    }

    private void ShowGuides(double? x, double? y)
    {
        GuideLayer.Children.Clear();

        if (x is null && y is null)
        {
            GuideLayer.IsVisible = false;
            return;
        }

        var brush = new SolidColorBrush(Color.Parse("#B4E5A44C"));

        if (x is { } vertical)
        {
            GuideLayer.Children.Add(new Line
            {
                StartPoint = new Point(vertical, 0),
                EndPoint = new Point(vertical, Height),
                Stroke = brush,
                StrokeThickness = 1,
                StrokeDashArray = [4, 3]
            });
        }

        if (y is { } horizontal)
        {
            GuideLayer.Children.Add(new Line
            {
                StartPoint = new Point(0, horizontal),
                EndPoint = new Point(Width, horizontal),
                Stroke = brush,
                StrokeThickness = 1,
                StrokeDashArray = [4, 3]
            });
        }

        GuideLayer.IsVisible = true;
    }

    private void ClearGuides()
    {
        GuideLayer.Children.Clear();
        GuideLayer.IsVisible = false;
    }

    private void OnToolbarDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ShowToolLayoutMenu();
            e.Handled = true;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _draggingToolbar = true;
        _toolbarManual = true;
        _toolbarDragStart = e.GetPosition(this);

        MeasureActionBar();
        _toolbarStartPosition = new Point(
            Canvas.GetLeft(ActionBar),
            Canvas.GetTop(ActionBar));
        e.Handled = true;
    }

    private void MoveToolbar(Point point)
    {
        var size = ActionBar.Bounds.Width > 0 ? ActionBar.Bounds.Size : ActionBar.DesiredSize;
        var screen = ScreenDipFor(_selection) ?? new Rect(0, 0, Width, Height);

        var left = Math.Clamp(
            _toolbarStartPosition.X + (point.X - _toolbarDragStart.X),
            screen.X + 8,
            Math.Max(screen.X + 8, screen.Right - size.Width - 8));
        var top = Math.Clamp(
            _toolbarStartPosition.Y + (point.Y - _toolbarDragStart.Y),
            screen.Y + 8,
            Math.Max(screen.Y + 8, screen.Bottom - size.Height - 8));

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, top);
    }

    private void ShowActionBar(Rect selection)
    {
        ActionBar.IsVisible = true;
        HintBar.IsVisible = false;
        CrosshairLayer.IsVisible = false;
        PositionActionBar(selection);
    }

    /// <summary>
    /// 工具条位置：对齐选区右下角，放得下就放下方，放不下翻到上方，
    /// 最后横向/纵向钳制在窗口内（参考 snow-shot 的 anchoredToolbarPlacement）。
    /// 用实测尺寸而不是估算宽度——按钮数量一变估算就失效。
    /// </summary>
    private bool _compactToolbar;

    /// <summary>0 = 完整，1 = 低频动作进更多，2 = 连 OCR/翻译动作也收起。</summary>
    private int _toolbarDensity;

    private bool _densityApplied;
    private bool _ocrActionsAvailable;
    private bool _translateAvailable;

    /// <summary>完整动作状态下的主条宽度；按钮可见性变化时置 NaN 重测。</summary>
    private double _fullMainBarWidth = double.NaN;

    /// <summary>一级压缩后的主条宽度（OCR/翻译仍在条上）。</summary>
    private double _compactMainBarWidth = double.NaN;

    /// <summary>
    /// 窗口窄于这个宽度（DIP）就把低频动作收起来。
    /// 工具组折叠之后工具条实测 678 宽，加两侧各 8 的边距需要 694，阈值定在 700。
    /// </summary>
    internal static bool ShouldCompact(double width) => width > 0 && width < 700;

    /// <summary>
    /// 窄窗口把低频动作收进「更多」。
    /// 学 snow-shot 的 layout compaction，只是规则更简单（按窗口宽度，而不是逐项量宽）。
    /// </summary>
    private void ApplyToolbarCompaction()
    {
        var available = ToolbarAvailableWidth;

        // 完整/一级压缩宽度只在按钮可见性变化时重测，避免拖动选区时每帧切换可见性
        if (double.IsNaN(_fullMainBarWidth))
        {
            SetToolbarDensity(0);
            MainBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _fullMainBarWidth = MainBar.DesiredSize.Width;

            SetToolbarDensity(1);
            MainBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _compactMainBarWidth = MainBar.DesiredSize.Width;
        }

        var level = _fullMainBarWidth <= available
            ? 0
            : _compactMainBarWidth <= available
                ? 1
                : 2;

        SetToolbarDensity(level);
    }

    internal void SetToolbarCompaction(bool compact) => SetToolbarDensity(compact ? 1 : 0);

    internal void SetToolbarDensity(int level)
    {
        level = Math.Clamp(level, 0, 2);

        if (level == _toolbarDensity && _densityApplied)
            return;

        _toolbarDensity = level;
        _compactToolbar = level > 0;
        _densityApplied = true;
        ApplyDensityVisibility();
    }

    private void ApplyDensityVisibility()
    {
        var compact = _toolbarDensity > 0;

        // ✓ 是主操作，任何宽度都保留；低频动作收进「更多」
        SaveButton.IsVisible = !compact;
        PinButton.IsVisible = !compact;
        ScrollButton.IsVisible = !compact;
        CornerRadiusButton.IsVisible = !compact;
        MoreButton.IsVisible = compact;

        // 二级压缩：收起 OCR 辅助动作和状态文字；翻译是主功能，保留
        OcrTextBar.IsVisible = _ocrActionsAvailable && _toolbarDensity < 2;
        TranslateActionButton.IsVisible = _translateAvailable;
        TranslateButton.IsVisible = _translateAvailable && _translatedPng is not null;
        OcrBoxButton.IsVisible = _ocrActionsAvailable && _toolbarDensity < 2;

        if (_toolbarDensity >= 2)
            OcrStatus.IsVisible = false;
    }

    private void InvalidateToolbarMetrics()
    {
        _fullMainBarWidth = double.NaN;
        _compactMainBarWidth = double.NaN;
    }

    private void PositionActionBar(Rect selection)
    {
        if (!ActionBar.IsVisible)
            return;

        ApplyToolbarCompaction();

        // 手动拖开后，本次「选区不变」期间保持手动位置；
        // 一旦选区变动（ApplyCreate/Move/Resize）就复位，重新贴回选区
        if (_toolbarManual)
        {
            if (!_toolbarPlacementApplied)
                ApplyToolbarFraction(selection);

            return;
        }

        MeasureActionBar();
        var size = ActionBar.DesiredSize;

        var badge = ComputeSizeBadgePlacement(selection);
        var bounds = ScreenDipFor(selection) ?? new Rect(0, 0, Width, Height);
        var placement = ComputeToolbarPlacement(
            selection,
            size,
            bounds,
            new Rect(badge.Position, badge.Size));

        // 翻到选区上方时，属性栏/译文栏也翻到主条上侧，远离选区
        ApplyBarOrder(placement.Above);

        Canvas.SetLeft(ActionBar, placement.Position.X);
        Canvas.SetTop(ActionBar, placement.Position.Y);
        _toolbarPlacementApplied = true;
    }

    /// <summary>
    /// 工具条翻到选区上方时，把属性栏/译文栏也翻到主条上侧（远离选区），
    /// 避免压住选区边缘那块内容；回到下方时恢复「主条 → 属性栏 → 译文栏」。
    /// 参考 snow-shot 的 usesTopRightPlacement。
    /// </summary>
    private void ApplyBarOrder(bool above)
    {
        if (_barOrderApplied && above == _barAbove)
            return;

        // 上方：主条最靠近选区，附属行依次向外；下方：主条在最外侧
        var order = above
            ? new Control[] { RecordingRow, TransRow, StyleRow, MainBar }
            : new Control[] { MainBar, StyleRow, TransRow, RecordingRow };

        for (var i = 0; i < order.Length; i++)
        {
            var index = ActionBar.Children.IndexOf(order[i]);
            if (index >= 0 && index != i)
                ActionBar.Children.Move(index, i);
        }

        _barAbove = above;
        _barOrderApplied = true;
    }

    /// <summary>用户拖过工具条后，把位置记成当前显示器的 0–1 比例。</summary>
    private void RememberToolbarPlacement()
    {
        var size = ActionBar.Bounds.Width > 0 ? ActionBar.Bounds.Size : ActionBar.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var left = Canvas.GetLeft(ActionBar);
        var top = Canvas.GetTop(ActionBar);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        var screen = ScreenDipFor(_selection) ?? new Rect(0, 0, Width, Height);
        _toolbarFraction = RememberToolbarFraction(
            screen, size, new Point(left, top));
        _toolbarPlacementApplied = true;
    }

    private void ApplyToolbarFraction(Rect selection)
    {
        _toolbarPlacementApplied = true;

        if (_toolbarFraction is not { } fraction)
            return;

        MeasureActionBar();
        var size = ActionBar.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            _toolbarPlacementApplied = false;
            return;
        }

        var screen = ScreenDipFor(selection) ?? new Rect(0, 0, Width, Height);
        var position = RestoreToolbarPosition(screen, size, fraction);
        Canvas.SetLeft(ActionBar, position.X);
        Canvas.SetTop(ActionBar, position.Y);
    }

    internal static Point RememberToolbarFraction(Rect screen, Size toolbar, Point position)
    {
        var rangeX = Math.Max(1, screen.Width - toolbar.Width);
        var rangeY = Math.Max(1, screen.Height - toolbar.Height);

        return new Point(
            Math.Clamp((position.X - screen.X) / rangeX, 0, 1),
            Math.Clamp((position.Y - screen.Y) / rangeY, 0, 1));
    }

    internal static Point RestoreToolbarPosition(Rect screen, Size toolbar, Point fraction)
    {
        var rangeX = Math.Max(0, screen.Width - toolbar.Width);
        var rangeY = Math.Max(0, screen.Height - toolbar.Height);

        return new Point(
            screen.X + Math.Clamp(fraction.X, 0, 1) * rangeX,
            screen.Y + Math.Clamp(fraction.Y, 0, 1) * rangeY);
    }

    /// <summary>工具条可用宽度：留 8px 边距，属性栏按这个宽度折行。</summary>
    private double ToolbarAvailableWidth
    {
        get
        {
            var width = Width > 0 ? Width : 800;
            return Math.Max(240, width - 16);
        }
    }

    /// <summary>约束宽度后再测量，属性栏才能按整组换行。</summary>
    private void MeasureActionBar()
    {
        var available = ToolbarAvailableWidth;
        ActionBar.MaxWidth = available;
        ActionBar.Measure(new Size(available, double.PositiveInfinity));
    }

    /// <summary>布局完成后再按实际 Bounds 校正一次居中，避免用到上一帧的尺寸。</summary>
    private void OnActionBarLayoutUpdated(object? sender, EventArgs e)
    {
        if (!ActionBar.IsVisible || _toolbarManual)
            return;

        var size = ActionBar.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var badge = ComputeSizeBadgePlacement(_selection);
        var screen = ScreenDipFor(_selection) ?? new Rect(0, 0, Width, Height);
        var placement = ComputeToolbarPlacement(
            _selection,
            size,
            screen,
            new Rect(badge.Position, badge.Size));

        ApplyBarOrder(placement.Above);

        var currentLeft = Canvas.GetLeft(ActionBar);
        var currentTop = Canvas.GetTop(ActionBar);

        if (Math.Abs(currentLeft - placement.Position.X) < 0.5 && Math.Abs(currentTop - placement.Position.Y) < 0.5)
            return;

        Canvas.SetLeft(ActionBar, placement.Position.X);
        Canvas.SetTop(ActionBar, placement.Position.Y);
    }

    internal static Point ComputeToolbarPosition(
        Rect selection,
        Size toolbar,
        Size bounds,
        Rect? avoid = null,
        double gap = 8,
        double margin = 8) =>
        ComputeToolbarPlacement(
            selection,
            toolbar,
            new Rect(0, 0, bounds.Width, bounds.Height),
            avoid,
            gap,
            margin).Position;

    internal static Point ComputeToolbarPosition(
        Rect selection,
        Size toolbar,
        Rect bounds,
        Rect? avoid = null,
        double gap = 8,
        double margin = 8) =>
        ComputeToolbarPlacement(selection, toolbar, bounds, avoid, gap, margin).Position;

    /// <summary>工具条位置 + 是否翻到了选区上方（附属行要跟着翻，远离选区）。</summary>
    internal readonly record struct ToolbarPlacement(Point Position, bool Above);

    internal static ToolbarPlacement ComputeToolbarPlacement(
        Rect selection,
        Size toolbar,
        Rect bounds,
        Rect? avoid = null,
        double gap = 8,
        double margin = 8)
    {
        var minX = bounds.X + margin;
        var maxX = Math.Max(minX, bounds.Right - toolbar.Width - margin);
        var minY = bounds.Y + margin;
        var maxY = Math.Max(minY, bounds.Bottom - toolbar.Height - margin);

        // 锚定在选区下方并水平居中
        var clampedX = Math.Clamp(selection.Center.X - toolbar.Width / 2, minX, maxX);

        bool Fits(Point p)
        {
            if (p.X < minX || p.Y < minY || p.X > maxX || p.Y > maxY)
                return false;

            if (avoid is { } rect)
                return !new Rect(p, toolbar).Intersects(rect);

            return true;
        }

        var below = new Point(clampedX, selection.Bottom + gap);
        if (Fits(below))
            return new ToolbarPlacement(below, false);

        var above = new Point(clampedX, selection.Top - toolbar.Height - gap);
        if (Fits(above))
            return new ToolbarPlacement(above, true);

        if (avoid is { } badge && badge.Width > 0 && badge.Height > 0)
        {
            // 上方被尺寸浮条挡住：叠到浮条上方
            var aboveBadge = new Point(clampedX, badge.Top - toolbar.Height - gap);
            if (Fits(aboveBadge))
                return new ToolbarPlacement(aboveBadge, true);

            // 再试选区左右两侧
            var sideY = Math.Clamp(selection.Center.Y - toolbar.Height / 2, minY, maxY);

            var rightSide = new Point(selection.Right + gap, sideY);
            if (Fits(rightSide))
                return new ToolbarPlacement(rightSide, false);

            var leftSide = new Point(selection.Left - toolbar.Width - gap, sideY);
            if (Fits(leftSide))
                return new ToolbarPlacement(leftSide, false);
        }

        // 上下都放不下：下方优先，整体钳制，保持确定性
        return new ToolbarPlacement(
            new Point(clampedX, Math.Clamp(below.Y, minY, maxY)), false);
    }

    private bool IsPointInActionBar(Point point)
    {
        if (!ActionBar.IsVisible)
            return false;

        if (ActionBar.TranslatePoint(new Point(0, 0), this) is not { } origin)
            return false;

        return new Rect(origin, ActionBar.Bounds.Size).Contains(point);
    }

    private bool IsPointInCustomColorPanel(Point point)
    {
        if (!CustomColorPanel.IsVisible)
            return false;

        if (CustomColorPanel.TranslatePoint(new Point(0, 0), this) is not { } origin)
            return false;

        return new Rect(origin, CustomColorPanel.Bounds.Size).Contains(point);
    }
}
