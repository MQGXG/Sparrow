using Avalonia;
using Avalonia.Controls.Primitives;

namespace Sparrow.Plugin.Ui;

/// <summary>
/// 宿主提供的「标签 + 数字输入」。用于起止时间、帧号这类数值参数；
/// 控件外观和主题由宿主样式负责，插件只声明标签与范围。
/// </summary>
public class PluginNumberField : TemplatedControl
{
    /// <summary>输入框上方的标签。</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<PluginNumberField, string?>(nameof(Label));

    /// <summary>当前数值。</summary>
    public static readonly StyledProperty<decimal?> ValueProperty =
        AvaloniaProperty.Register<PluginNumberField, decimal?>(nameof(Value));

    /// <summary>允许的最小值。</summary>
    public static readonly StyledProperty<decimal> MinimumProperty =
        AvaloniaProperty.Register<PluginNumberField, decimal>(nameof(Minimum), decimal.MinValue);

    /// <summary>允许的最大值。</summary>
    public static readonly StyledProperty<decimal> MaximumProperty =
        AvaloniaProperty.Register<PluginNumberField, decimal>(nameof(Maximum), decimal.MaxValue);

    /// <summary>每次增减的步长。</summary>
    public static readonly StyledProperty<decimal> IncrementProperty =
        AvaloniaProperty.Register<PluginNumberField, decimal>(nameof(Increment), 1m);

    /// <summary>数字显示格式，例如 <c>0.###</c>；为空时用控件默认格式。</summary>
    public static readonly StyledProperty<string> FormatStringProperty =
        AvaloniaProperty.Register<PluginNumberField, string>(nameof(FormatString), string.Empty);

    /// <summary>输入框上方的标签。</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>当前数值。</summary>
    public decimal? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>允许的最小值。</summary>
    public decimal Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>允许的最大值。</summary>
    public decimal Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>每次增减的步长。</summary>
    public decimal Increment
    {
        get => GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    /// <summary>数字显示格式。</summary>
    public string FormatString
    {
        get => GetValue(FormatStringProperty);
        set => SetValue(FormatStringProperty, value);
    }
}
