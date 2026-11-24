// CaptureWindow 分部类：工具按钮点击、颜色应用与调色板（SV/Hue/Alpha/HEX/预设色）。
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
    // ---------- 标注 ----------

    /// <summary>指针工具：退出标注模式，回到移动/拉伸选区。</summary>
    private void OnSelectToolClick(object? sender, RoutedEventArgs e)
    {
        _tool = AnnotationTool.None;
        UpdateToolButtons();
    }

    private void OnRectToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Rectangle);
    private void OnEllipseToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Ellipse);
    private void OnDiamondToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Diamond);

    private void OnArrowToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Arrow);

    private void OnLineToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Line);

    private void OnPenToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Pen);

    private void OnHighlighterToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Highlighter);

    private void OnMosaicToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Mosaic);

    private void OnEraserToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Eraser);

    private void OnSpotlightToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Spotlight);

    private void OnMagnifierToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Magnifier);

    private void OnWatermarkToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Watermark);

    private void OnDistanceToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Distance);

    private void OnBracketToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Bracket);

    private void OnBubbleToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Bubble);

    private void OnAngleToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Angle);

    private void OnTextToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Text);

    private void OnNumberToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Number);

    private void OnColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            ApplyColor(ColorTarget.Stroke, Color.Parse(hex));
            UpdateStyleButtons();
        }
    }

    /// <summary>描边/填充共用的颜色写回：填充自动切成实心，避免选了颜色看不到效果。</summary>
    private void ApplyColor(ColorTarget target, Color color)
    {
        var value = color.ToUInt32();

        if (target == ColorTarget.Fill)
        {
            if (ActiveStyleTool == AnnotationTool.Bubble)
            {
                _bubbleFillColor = value;
                if (_bubbleFill == ShapeFill.None)
                    _bubbleFill = ShapeFill.Solid;
            }
            else
            {
                _noteFillColor = value;
                if (_noteFill == ShapeFill.None)
                    _noteFill = ShapeFill.Solid;
            }

            var fill = ActiveStyleTool == AnnotationTool.Bubble ? _bubbleFill : _noteFill;
            ApplyNoteStyle(note => AnnotationStyler.WithFillColor(
                AnnotationStyler.WithFill(note, fill), value));
            return;
        }

        _noteColor = color;
        ApplyNoteStyle(note => AnnotationStyler.WithColor(note, value));
    }

    // ---------- 自定义颜色 ----------

    private enum ColorTarget
    {
        Stroke,
        Fill
    }

    private readonly List<Color> _recentColors = [];
    private double _pickerHue;
    private double _pickerSaturation = 1;
    private double _pickerValue = 1;
    private double _pickerAlpha = 1;
    private Color _pickerColor = NoteColor;
    private ColorTarget _pickerTarget = ColorTarget.Stroke;

    private void OnCustomColorClick(object? sender, RoutedEventArgs e)
    {
        if (CustomColorPanel.IsVisible && _pickerTarget == ColorTarget.Stroke)
        {
            CustomColorPanel.IsVisible = false;
            return;
        }

        _pickerTarget = ColorTarget.Stroke;
        ShowColorPanel();
    }

    private void OnFillColorClick(object? sender, RoutedEventArgs e)
    {
        if (CustomColorPanel.IsVisible && _pickerTarget == ColorTarget.Fill)
        {
            CustomColorPanel.IsVisible = false;
            return;
        }

        _pickerTarget = ColorTarget.Fill;
        ShowColorPanel();
    }

    private void ShowColorPanel()
    {
        var source = _pickerTarget == ColorTarget.Fill
            ? FillColorValue
            : _noteColor;

        _pickerColor = source;
        _pickerAlpha = _noteOpacity;
        (_pickerHue, _pickerSaturation, _pickerValue) = RgbToHsv(source);

        ColorPanelTitle.Text = _pickerTarget == ColorTarget.Fill ? "填充颜色" : "描边颜色";
        // 线型只属于描边；填充面板不显示描边线型
        DashGroup.IsVisible = _pickerTarget == ColorTarget.Stroke && SupportsDash(ActiveStyleTool);
        RenderColorPicker();

        CustomColorPanel.IsVisible = true;
        CustomColorPanel.Measure(Size.Infinity);
        var size = CustomColorPanel.DesiredSize;

        var anchorLeft = Canvas.GetLeft(ActionBar);
        var anchorTop = Canvas.GetTop(ActionBar);
        if (double.IsNaN(anchorLeft))
            anchorLeft = 8;
        if (double.IsNaN(anchorTop))
            anchorTop = Height - size.Height - 8;

        var left = Math.Clamp(anchorLeft + 8, 8, Math.Max(8, Width - size.Width - 8));
        var top = Math.Clamp(anchorTop - size.Height - 8, 8, Math.Max(8, Height - size.Height - 8));

        Canvas.SetLeft(CustomColorPanel, left);
        Canvas.SetTop(CustomColorPanel, top);
    }

    private void OnCloseColorPanel(object? sender, RoutedEventArgs e) =>
        CustomColorPanel.IsVisible = false;

    private void OnSvPressed(object? sender, PointerPressedEventArgs e) =>
        UpdateSaturationValue(e.GetPosition(SvArea));

    private void OnSvMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(SvArea).Properties.IsLeftButtonPressed)
            UpdateSaturationValue(e.GetPosition(SvArea));
    }

    private void UpdateSaturationValue(Point point)
    {
        var width = Math.Max(1, SvArea.Bounds.Width);
        var height = Math.Max(1, SvArea.Bounds.Height);

        _pickerSaturation = Math.Clamp(point.X / width, 0, 1);
        _pickerValue = 1 - Math.Clamp(point.Y / height, 0, 1);
        RenderColorPicker();
    }

    private void OnHuePressed(object? sender, PointerPressedEventArgs e) =>
        UpdateHue(e.GetPosition(HueBar));

    private void OnHueMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(HueBar).Properties.IsLeftButtonPressed)
            UpdateHue(e.GetPosition(HueBar));
    }

    private void UpdateHue(Point point)
    {
        var width = Math.Max(1, HueBar.Bounds.Width);
        _pickerHue = 360 * Math.Clamp(point.X / width, 0, 1);
        RenderColorPicker();
    }

    private void RenderColorPicker()
    {
        _pickerColor = HsvToRgb(_pickerHue, _pickerSaturation, _pickerValue);
        HueBase.Background = new SolidColorBrush(HsvToRgb(_pickerHue, 1, 1));

        // 用户正在 HEX 框里输入时不覆盖，避免每次渲染打断输入
        if (!HexBox.IsFocused)
            HexBox.Text = $"#{_pickerColor.R:X2}{_pickerColor.G:X2}{_pickerColor.B:X2}";
        AlphaText.Text = $"{_pickerAlpha * 100:0}%";

        // 透明度条：当前色的「透明 → 不透明」渐变，底下透出棋盘格
        AlphaGradient.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(0, _pickerColor.R, _pickerColor.G, _pickerColor.B), 0),
                new GradientStop(Color.FromArgb(255, _pickerColor.R, _pickerColor.G, _pickerColor.B), 1)
            }
        };

        // 面板刚显示时 Bounds 还是 0：等布局完成再摆一次，否则光标会先落在兜底坐标上
        if (SvArea.Bounds.Width <= 1 || SvArea.Bounds.Height <= 1)
        {
            if (CustomColorPanel.IsVisible)
                Dispatcher.UIThread.Post(RenderColorPicker, DispatcherPriority.Loaded);

            return;
        }

        var width = SvArea.Bounds.Width;
        var height = SvArea.Bounds.Height;

        SvCursor.Margin = new Thickness(
            _pickerSaturation * width - 6,
            (1 - _pickerValue) * height - 6,
            0,
            0);

        var alphaWidth = AlphaBar.Bounds.Width > 1 ? AlphaBar.Bounds.Width : 232;
        AlphaCursor.Margin = new Thickness(_pickerAlpha * alphaWidth - 6, 0, 0, 0);
    }

    private void OnAlphaPressed(object? sender, PointerPressedEventArgs e) =>
        UpdateAlpha(e.GetPosition(AlphaBar));

    private void OnAlphaMoved(object? sender, PointerEventArgs e)
    {
        if (e.GetCurrentPoint(AlphaBar).Properties.IsLeftButtonPressed)
            UpdateAlpha(e.GetPosition(AlphaBar));
    }

    private void UpdateAlpha(Point point)
    {
        var width = Math.Max(1, AlphaBar.Bounds.Width);
        _pickerAlpha = Math.Clamp(point.X / width, 0, 1);
        RenderColorPicker();
    }

    private void OnHexBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyHexInput();
            e.Handled = true;
        }
    }

    private void OnHexBoxLostFocus(object? sender, RoutedEventArgs e) => ApplyHexInput();

    /// <summary>解析 HEX 输入并同步到当前取色；非法输入直接还原显示。</summary>
    private void ApplyHexInput()
    {
        var text = HexBox.Text?.Trim() ?? string.Empty;
        if (text.Length > 0 && !text.StartsWith('#'))
            text = "#" + text;

        if (text.Length > 0 && Color.TryParse(text, out var color))
        {
            _pickerColor = color;
            (_pickerHue, _pickerSaturation, _pickerValue) = RgbToHsv(color);
        }

        RenderColorPicker();
    }

    private void OnApplyCustomColor(object? sender, RoutedEventArgs e)
    {
        ApplyColor(_pickerTarget, _pickerColor);

        if (_pickerTarget == ColorTarget.Stroke)
        {
            _noteOpacity = _pickerAlpha;
            PushRecentColor(_pickerColor);
        }

        UpdateStyleButtons();
        CustomColorPanel.IsVisible = false;
    }

    /// <summary>预设色板：Ant Design 常用色，点一下直接进取色。</summary>
    private static readonly Color[] PresetColorPalette =
    [
        Color.Parse("#F5222D"),
        Color.Parse("#FA8C16"),
        Color.Parse("#FADB14"),
        Color.Parse("#52C41A"),
        Color.Parse("#13C2C2"),
        Color.Parse("#1677FF"),
        Color.Parse("#722ED1"),
        Color.Parse("#EB2F96"),
        Color.Parse("#000000"),
        Color.Parse("#FFFFFF")
    ];

    private void BuildPresetColors()
    {
        PresetColors.Children.Clear();

        foreach (var preset in PresetColorPalette)
        {
            var picked = preset;
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(picked),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                Margin = new Thickness(0, 0, 4, 4)
            };
            swatch.Click += (_, _) =>
            {
                _pickerColor = picked;
                (_pickerHue, _pickerSaturation, _pickerValue) = RgbToHsv(picked);
                RenderColorPicker();
            };
            PresetColors.Children.Add(swatch);
        }
    }

    private void PushRecentColor(Color color)
    {
        _recentColors.RemoveAll(item => item.ToUInt32() == color.ToUInt32());
        _recentColors.Insert(0, color);

        if (_recentColors.Count > 8)
            _recentColors.RemoveAt(_recentColors.Count - 1);

        BuildRecentColors();
    }

    private void BuildRecentColors()
    {
        RecentColors.Children.Clear();

        foreach (var color in _recentColors)
        {
            var picked = color;
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#66000000")),
                Margin = new Thickness(0, 0, 6, 6)
            };

            ToolTip.SetTip(swatch, $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}");
            swatch.Click += (_, _) =>
            {
                _pickerColor = picked;
                (_pickerHue, _pickerSaturation, _pickerValue) = RgbToHsv(picked);
                RenderColorPicker();
            };

            RecentColors.Children.Add(swatch);
        }
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;

        if (delta > 0)
        {
            if (max == r)
                hue = 60 * (((g - b) / delta) % 6);
            else if (max == g)
                hue = 60 * (((b - r) / delta) + 2);
            else
                hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
            hue += 360;

        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;

        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
