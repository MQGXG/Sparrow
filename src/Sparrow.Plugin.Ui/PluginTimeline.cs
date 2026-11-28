using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Sparrow.Plugin.Ui;

/// <summary>
/// 宿主提供的区间选择条：一条轨道 + 选中区间 + 左右两个可拖动把手。
///
/// 单位统一为秒，只负责「选起止区间」，不读媒体、不画波形。
/// 颜色全部来自宿主主题（<see cref="TrackBrush"/> 等），插件不硬编码。
/// </summary>
public class PluginTimeline : TemplatedControl
{
    private const double HandleWidth = 10;
    private const double HandleHeight = 18;
    private const double TrackHeight = 6;
    private const double KeyboardStep = 0.1;

    /// <summary>轨道总时长（秒）。小于等于 0 时按 1 秒处理，避免除零。</summary>
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<PluginTimeline, double>(nameof(Duration));

    /// <summary>选区起点（秒），夹在 <c>[0, Duration]</c>。</summary>
    public static readonly StyledProperty<double> StartProperty =
        AvaloniaProperty.Register<PluginTimeline, double>(nameof(Start));

    /// <summary>选区终点（秒），夹在 <c>[Start, Duration]</c>。</summary>
    public static readonly StyledProperty<double> EndProperty =
        AvaloniaProperty.Register<PluginTimeline, double>(nameof(End));

    /// <summary>轨道底色。</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<PluginTimeline, IBrush?>(nameof(TrackBrush));

    /// <summary>选中区间的填充色。</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<PluginTimeline, IBrush?>(nameof(SelectionBrush));

    /// <summary>把手的填充色。</summary>
    public static readonly StyledProperty<IBrush?> HandleBrushProperty =
        AvaloniaProperty.Register<PluginTimeline, IBrush?>(nameof(HandleBrush));

    /// <summary>把手的描边色。</summary>
    public static readonly StyledProperty<IBrush?> HandleBorderBrushProperty =
        AvaloniaProperty.Register<PluginTimeline, IBrush?>(nameof(HandleBorderBrush));

    private Border? _track;
    private Border? _selection;
    private Border? _startHandle;
    private Border? _endHandle;

    private bool _normalizing;
    private bool _dragging;

    /// <summary>当前拖动 / 键盘微调的是不是左把手；首次按下前默认左把手。</summary>
    private bool _dragStart = true;

    public PluginTimeline()
    {
        // 方向键微调需要焦点。
        Focusable = true;
    }

    /// <summary>轨道总时长（秒）。小于等于 0 时按 1 秒处理。</summary>
    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>选区起点（秒）。</summary>
    public double Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    /// <summary>选区终点（秒）。</summary>
    public double End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    /// <summary>轨道底色。</summary>
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>选中区间的填充色。</summary>
    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>把手的填充色。</summary>
    public IBrush? HandleBrush
    {
        get => GetValue(HandleBrushProperty);
        set => SetValue(HandleBrushProperty, value);
    }

    /// <summary>把手的描边色。</summary>
    public IBrush? HandleBorderBrush
    {
        get => GetValue(HandleBorderBrushProperty);
        set => SetValue(HandleBorderBrushProperty, value);
    }

    /// <summary>实际参与计算的时长；<see cref="Duration"/> 非正时回退到 1 秒。</summary>
    private double EffectiveDuration => Duration > 0 ? Duration : 1;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _track = e.NameScope.Find<Border>("PART_Track");
        _selection = e.NameScope.Find<Border>("PART_Selection");
        _startHandle = e.NameScope.Find<Border>("PART_StartHandle");
        _endHandle = e.NameScope.Find<Border>("PART_EndHandle");

        UpdateVisuals(Bounds.Size);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (_normalizing)
            return;

        if (change.Property == StartProperty
            || change.Property == EndProperty
            || change.Property == DurationProperty)
        {
            Normalize(change.Property);
            UpdateVisuals(Bounds.Size);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 先按最终宽度摆好轨道与把手，再让模板完成这一轮排布。
        UpdateVisuals(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus();

        // 离哪个把手近就拖哪个；点在轨道中间也能落到最近的一端。
        var x = e.GetPosition(this).X;
        _dragStart = Math.Abs(x - HandleX(Start)) <= Math.Abs(x - HandleX(End));
        _dragging = true;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging)
            return;

        var seconds = SecondsAt(e.GetPosition(this).X);

        // 拖动时直接夹住当前把手，不允许越过另一端。
        if (_dragStart)
            Start = Math.Clamp(seconds, 0, End);
        else
            End = Math.Clamp(seconds, Start, EffectiveDuration);

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key is not (Key.Left or Key.Right))
            return;

        // Shift 加速到 1 秒；默认 0.1 秒，方便对齐。
        var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 1 : KeyboardStep;
        var delta = e.Key == Key.Right ? step : -step;

        if (_dragStart)
            Start = Math.Clamp(Start + delta, 0, End);
        else
            End = Math.Clamp(End + delta, Start, EffectiveDuration);

        e.Handled = true;
    }

    /// <summary>
    /// 夹取并保证不反向：设置哪一端，就让另一端跟着走。
    /// 这样外部先设 Start 再设 End、或反过来，都不会出现反向区间。
    /// </summary>
    private void Normalize(AvaloniaProperty changed)
    {
        var max = EffectiveDuration;
        var start = Math.Clamp(Start, 0, max);
        var end = Math.Clamp(End, 0, max);

        if (changed == StartProperty)
        {
            if (start > end)
                end = start;
        }
        else if (end < start)
        {
            start = end;
        }

        _normalizing = true;
        try
        {
            if (!start.Equals(Start))
                Start = start;

            if (!end.Equals(End))
                End = end;
        }
        finally
        {
            _normalizing = false;
        }
    }

    /// <summary>按当前尺寸摆轨道、选中区间与两个把手。</summary>
    private void UpdateVisuals(Size size)
    {
        if (_track is null || _selection is null || _startHandle is null || _endHandle is null)
            return;

        var width = size.Width;
        if (width <= 0)
            return;

        var height = size.Height > 0 ? size.Height : 32;
        var left = HandleWidth / 2;
        var usable = Math.Max(1, width - HandleWidth);
        var centerY = height / 2;

        var startX = left + usable * (Start / EffectiveDuration);
        var endX = left + usable * (End / EffectiveDuration);

        _track.Width = usable;
        _track.Height = TrackHeight;
        Canvas.SetLeft(_track, left);
        Canvas.SetTop(_track, centerY - TrackHeight / 2);

        _selection.Width = Math.Max(0, endX - startX);
        _selection.Height = TrackHeight;
        Canvas.SetLeft(_selection, startX);
        Canvas.SetTop(_selection, centerY - TrackHeight / 2);

        SetHandle(_startHandle, startX, centerY);
        SetHandle(_endHandle, endX, centerY);
    }

    private static void SetHandle(Border handle, double centerX, double centerY)
    {
        handle.Width = HandleWidth;
        handle.Height = HandleHeight;
        Canvas.SetLeft(handle, centerX - HandleWidth / 2);
        Canvas.SetTop(handle, centerY - HandleHeight / 2);
    }

    /// <summary>把秒数换成控件坐标；只用于命中判断。</summary>
    private double HandleX(double seconds)
    {
        var usable = Math.Max(1, Bounds.Width - HandleWidth);
        return HandleWidth / 2 + usable * (seconds / EffectiveDuration);
    }

    /// <summary>把控件坐标换成秒数；可能越界，由调用方夹取。</summary>
    private double SecondsAt(double x)
    {
        var usable = Math.Max(1, Bounds.Width - HandleWidth);
        return (x - HandleWidth / 2) / usable * EffectiveDuration;
    }
}
