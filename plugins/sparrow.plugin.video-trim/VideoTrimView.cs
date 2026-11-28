using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Sparrow.Plugin.Ui;

namespace Sparrow.Plugin.Video.Trim;

/// <summary>
/// 视频裁剪的自定义表面：时间轴选起止区间，下面两个数字框做精确输入。
///
/// 契约里还没有「读取视频时长」的 API，轨道初始按 <c>max(60, End)</c> 秒显示；
/// 实际裁剪仍以参数里的起止秒为准，所以拖动 / 输入都会写回参数。
/// </summary>
public sealed class VideoTrimView : UserControl
{
    private const string ParameterStart = "start";
    private const string ParameterEnd = "end";
    private const double MinimumDuration = 60;

    /// <summary>数字框上限只防误输入；超过轨道长度时轨道会跟着变长。</summary>
    private const decimal MaximumSeconds = 86_400;

    private readonly IToolViewContext _context;
    private readonly PluginTimeline _timeline;
    private readonly PluginNumberField _startField;
    private readonly PluginNumberField _endField;

    /// <summary>时间轴与数字框互相回写时置位，避免事件套事件。</summary>
    private bool _syncing;

    public VideoTrimView(IToolViewContext context)
    {
        _context = context;

        var start = ReadSeconds(ParameterStart, 0);
        var end = ReadSeconds(ParameterEnd, 10);

        // 参数可能来自旧历史；界面先兜底成合法区间，执行时仍按参数原样校验。
        if (end < start)
            end = start;

        var duration = Math.Max(MinimumDuration, end);

        _timeline = new PluginTimeline
        {
            Duration = duration,
            Start = start,
            End = end
        };

        _startField = CreateField("开始（秒）", start);
        _endField = CreateField("结束（秒）", end);

        var inputs = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 8,
            Children =
            {
                _startField,
                _endField
            }
        };
        Grid.SetColumn(_endField, 1);

        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _timeline,
                inputs,
                new PluginHint
                {
                    Text = "时长未知时按 60 秒轨道显示；实际裁剪以起止秒为准。"
                }
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new PluginSection
                {
                    Title = "裁剪区间",
                    Content = body
                }
            }
        };

        // 初始值直接写进控件，不触发回写；之后才订阅用户交互。
        _timeline.PropertyChanged += OnTimelineChanged;
        _startField.PropertyChanged += OnStartFieldChanged;
        _endField.PropertyChanged += OnEndFieldChanged;
    }

    private static PluginNumberField CreateField(string label, double value) => new()
    {
        Label = label,
        Value = (decimal)value,
        Minimum = 0,
        Maximum = MaximumSeconds,
        Increment = 0.5m,
        FormatString = "0.###"
    };

    /// <summary>拖动把手：时间轴先夹好，再把起止同步到数字框与参数。</summary>
    private void OnTimelineChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncing
            || (e.Property != PluginTimeline.StartProperty && e.Property != PluginTimeline.EndProperty))
        {
            return;
        }

        _syncing = true;
        try
        {
            _startField.Value = (decimal)_timeline.Start;
            _endField.Value = (decimal)_timeline.End;
        }
        finally
        {
            _syncing = false;
        }

        WriteParameters();
    }

    /// <summary>精确输入起点：夹在 [0, End]，再回写时间轴与参数。</summary>
    private void OnStartFieldChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncing || e.Property != PluginNumberField.ValueProperty)
            return;

        if (_startField.Value is not { } value)
            return;

        _syncing = true;
        try
        {
            _timeline.Start = Math.Clamp((double)value, 0, _timeline.End);
            _startField.Value = (decimal)_timeline.Start;
        }
        finally
        {
            _syncing = false;
        }

        WriteParameters();
    }

    /// <summary>精确输入终点：超过轨道长度时先延长轨道，再夹在 [Start, Duration]。</summary>
    private void OnEndFieldChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncing || e.Property != PluginNumberField.ValueProperty)
            return;

        if (_endField.Value is not { } value)
            return;

        _syncing = true;
        try
        {
            if ((double)value > _timeline.Duration)
                _timeline.Duration = (double)value;

            _timeline.End = Math.Clamp((double)value, _timeline.Start, _timeline.Duration);
            _endField.Value = (decimal)_timeline.End;
        }
        finally
        {
            _syncing = false;
        }

        WriteParameters();
    }

    /// <summary>起止参数始终用不变文化写，避免逗号小数点进到 ffmpeg 参数里。</summary>
    private void WriteParameters()
    {
        _context.SetParameter(ParameterStart, FormatSeconds(_timeline.Start));
        _context.SetParameter(ParameterEnd, FormatSeconds(_timeline.End));
    }

    private static string FormatSeconds(double seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private double ReadSeconds(string key, double fallback)
    {
        var text = _context.GetParameter(key);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
               && value >= 0
            ? value
            : fallback;
    }
}
