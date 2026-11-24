// CaptureWindow 分部类：线宽、描边、填充、文字、水印、序号的样式控件与撤销重做。
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
    private void OnWidth2Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(2);
    private void OnWidth3Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(3);
    private void OnWidth4Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(4);
    private void OnWidth8Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(8);
    private void OnWidth12Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(12);

    private void SetNoteWidth(double width)
    {
        _noteWidth = width;
        ApplyNoteStyle(note => AnnotationStyler.WithWidth(note, width));
        UpdateStyleButtons();
    }

    private void OnDashSolidMenu(object? sender, RoutedEventArgs e)
        => SetLineStroke(LineDash.Solid, LineStyle.Neat);

    private void OnDashDashMenu(object? sender, RoutedEventArgs e)
        => SetLineStroke(LineDash.Dash, LineStyle.Neat);

    private void OnDashDotMenu(object? sender, RoutedEventArgs e)
        => SetLineStroke(LineDash.Dot, LineStyle.Neat);

    private void OnStyleSketchMenu(object? sender, RoutedEventArgs e)
        => SetLineStroke(LineDash.Solid, LineStyle.Sketch);

    private void OnStyleRoughMenu(object? sender, RoutedEventArgs e)
        => SetLineStroke(LineDash.Solid, LineStyle.Rough);

    /// <summary>
    /// 描边样式：线型和笔触是同一件事，统一成一个选项。
    /// 同时写回「下次新建」的默认值和当前标注（没选中时跟随刚画的那条）。
    /// </summary>
    internal void SetLineStroke(LineDash dash, LineStyle style)
    {
        _noteDash = dash;
        _noteStyle = style;

        ApplyNoteStyle(note => AnnotationStyler.WithLineStyle(
            AnnotationStyler.WithDash(note, dash), style));

        UpdateStyleButtons();
    }

    private void OnFillNoneMenu(object? sender, RoutedEventArgs e) => SetNoteFill(ShapeFill.None);
    private void OnFillSoftMenu(object? sender, RoutedEventArgs e) => SetNoteFill(ShapeFill.Soft);
    private void OnFillSolidMenu(object? sender, RoutedEventArgs e) => SetNoteFill(ShapeFill.Solid);
    private void OnFillLineMenu(object? sender, RoutedEventArgs e) => SetNoteFill(ShapeFill.Line);
    private void OnFillCrossMenu(object? sender, RoutedEventArgs e) => SetNoteFill(ShapeFill.CrossLine);

    /// <summary>点填充预设色 = 实心填充 + 该颜色。</summary>
    private void OnFillPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex })
            return;

        ApplyColor(ColorTarget.Fill, Color.Parse(hex));
        UpdateStyleButtons();
    }

    private void SetNoteFill(ShapeFill fill)
    {
        if (ActiveStyleTool == AnnotationTool.Bubble)
            _bubbleFill = fill;
        else
            _noteFill = fill;

        ApplyNoteStyle(note => AnnotationStyler.WithFill(note, fill));
        UpdateStyleButtons();
    }

    /// <summary>当前填充色：0 表示跟随描边色。</summary>
    private uint ActiveFillColorValue => ActiveStyleTool == AnnotationTool.Bubble ? _bubbleFillColor : _noteFillColor;

    private Color FillColorValue
    {
        get
        {
            var value = ActiveFillColorValue;
            return value != 0 ? Color.FromUInt32(value) : _noteColor;
        }
    }

    /// <summary>该工具/图元是否支持描边线型（文字、序号、气泡、聚光灯等没有）。</summary>
    private static bool SupportsDash(AnnotationTool tool) =>
        tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Diamond
            or AnnotationTool.Arrow or AnnotationTool.Line or AnnotationTool.Distance
            or AnnotationTool.Bracket or AnnotationTool.Pen or AnnotationTool.Highlighter;

    /// <summary>属性栏当前对应的工具：画图时是选中工具，选中图形时是该图形的类型。</summary>
    private AnnotationTool ActiveStyleTool =>
        _tool != AnnotationTool.None
            ? _tool
            : _selectedNote >= 0 && _selectedNote < _notes.Items.Count
                ? AnnotationStyler.ToolOf(_notes.Items[_selectedNote])
                : AnnotationTool.None;

    private double ActiveFontSize => ActiveStyleTool switch
    {
        AnnotationTool.Watermark => _watermarkFontSize,
        AnnotationTool.Bubble => _bubbleFontSize,
        _ => _noteFontSize
    };

    private void OnOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingStyleControls)
        {
            OpacityValue.Text = $"{e.NewValue:0}%";
            return;
        }

        var value = Math.Clamp(e.NewValue / 100, 0.2, 1);

        switch (ActiveStyleTool)
        {
            case AnnotationTool.Spotlight:
                _spotlightOpacity = value;
                break;
            case AnnotationTool.Watermark:
                _watermarkOpacity = value;
                break;
            default:
                _noteOpacity = value;
                break;
        }

        ApplyNoteStyle(note => AnnotationStyler.WithOpacity(note, value), history: false);
        OpacityValue.Text = $"{e.NewValue:0}%";
    }

    private void OnRadiusChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingStyleControls)
        {
            RadiusValue.Text = $"{e.NewValue:0} px";
            return;
        }

        _noteRadius = Math.Max(0, e.NewValue);
        ApplyNoteStyle(note => AnnotationStyler.WithRadius(note, _noteRadius), history: false);
        RadiusValue.Text = $"{e.NewValue:0} px";
    }

    private void OnFont14Menu(object? sender, RoutedEventArgs e) => SetFontSize(14);
    private void OnFont16Menu(object? sender, RoutedEventArgs e) => SetFontSize(16);
    private void OnFont20Menu(object? sender, RoutedEventArgs e) => SetFontSize(20);
    private void OnFont24Menu(object? sender, RoutedEventArgs e) => SetFontSize(24);
    private void OnFont32Menu(object? sender, RoutedEventArgs e) => SetFontSize(32);

    private void SetFontSize(double size)
    {
        switch (ActiveStyleTool)
        {
            case AnnotationTool.Watermark:
                _watermarkFontSize = size;
                break;
            case AnnotationTool.Bubble:
                _bubbleFontSize = size;
                break;
            default:
                _noteFontSize = size;
                break;
        }

        ApplyNoteStyle(note => AnnotationStyler.WithFontSize(note, size));
        UpdateStyleButtons();
    }

    private void OnBoldClick(object? sender, RoutedEventArgs e)
    {
        _noteBold = !_noteBold;
        ApplyNoteStyle(note => AnnotationStyler.WithBold(note, _noteBold));
        UpdateStyleButtons();
    }

    private void OnItalicClick(object? sender, RoutedEventArgs e)
    {
        _noteItalic = !_noteItalic;
        ApplyNoteStyle(note => AnnotationStyler.WithItalic(note, _noteItalic));
        UpdateStyleButtons();
    }

    private void OnAlignLeftMenu(object? sender, RoutedEventArgs e) => SetTextAlign(TextAlign.Left);
    private void OnAlignCenterMenu(object? sender, RoutedEventArgs e) => SetTextAlign(TextAlign.Center);
    private void OnAlignRightMenu(object? sender, RoutedEventArgs e) => SetTextAlign(TextAlign.Right);

    internal void SetTextAlign(TextAlign align)
    {
        _noteAlign = align;
        ApplyNoteStyle(note => AnnotationStyler.WithTextAlign(note, align));
        UpdateStyleButtons();
    }

    private void OnBackgroundNoneMenu(object? sender, RoutedEventArgs e) => SetTextBackground(0);
    private void OnBackgroundBlackMenu(object? sender, RoutedEventArgs e) => SetTextBackground(0xFF000000);
    private void OnBackgroundWhiteMenu(object? sender, RoutedEventArgs e) => SetTextBackground(0xFFFFFFFF);
    private void OnBackgroundAmberMenu(object? sender, RoutedEventArgs e) => SetTextBackground(0xFFE5A44C);

    internal void SetTextBackground(uint color)
    {
        _textBackground = color;
        ApplyNoteStyle(note => AnnotationStyler.WithTextBackground(note, color));
        UpdateStyleButtons();
    }

    private void OnStrokeWidth0Menu(object? sender, RoutedEventArgs e) => SetTextStrokeWidth(0);
    private void OnStrokeWidth1Menu(object? sender, RoutedEventArgs e) => SetTextStrokeWidth(1);
    private void OnStrokeWidth2Menu(object? sender, RoutedEventArgs e) => SetTextStrokeWidth(2);
    private void OnStrokeWidth4Menu(object? sender, RoutedEventArgs e) => SetTextStrokeWidth(4);

    internal void SetTextStrokeWidth(double width)
    {
        _textStrokeWidth = width;
        ApplyNoteStyle(note => AnnotationStyler.WithTextStroke(note, _textStrokeColor, width));
        UpdateStyleButtons();
    }

    private void OnStrokeColorBlackMenu(object? sender, RoutedEventArgs e) => SetTextStrokeColor(0xFF000000);
    private void OnStrokeColorWhiteMenu(object? sender, RoutedEventArgs e) => SetTextStrokeColor(0xFFFFFFFF);
    private void OnStrokeColorAmberMenu(object? sender, RoutedEventArgs e) => SetTextStrokeColor(0xFFE5A44C);

    internal void SetTextStrokeColor(uint color)
    {
        _textStrokeColor = color;
        ApplyNoteStyle(note => AnnotationStyler.WithTextStroke(note, color, _textStrokeWidth));
        UpdateStyleButtons();
    }

    /// <summary>字体下拉：程序化回填时不要当成用户选择。</summary>
    private void OnFontFamilyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingStyleControls || FontFamilyBox.SelectedItem is not string family)
            return;

        if (family == _noteFontFamily)
            return;

        _noteFontFamily = family;
        ApplyNoteStyle(note => AnnotationStyler.WithFontFamily(note, family));
    }

    private static string BackgroundLabel(uint color) =>
        color == 0 ? "无背景"
            : color == 0xFF000000 ? "黑底"
            : color == 0xFFFFFFFF ? "白底"
            : color == 0xFFE5A44C ? "琥珀底"
            : "自定义";

    private static string StrokeColorLabel(uint color) =>
        color == 0xFF000000 ? "黑"
            : color == 0xFFFFFFFF ? "白"
            : color == 0xFFE5A44C ? "琥珀"
            : "自定义";

    private void OnNumber16Menu(object? sender, RoutedEventArgs e) => SetNumberSize(16);
    private void OnNumber24Menu(object? sender, RoutedEventArgs e) => SetNumberSize(24);
    private void OnNumber32Menu(object? sender, RoutedEventArgs e) => SetNumberSize(32);
    private void OnNumber48Menu(object? sender, RoutedEventArgs e) => SetNumberSize(48);

    private void SetNumberSize(double size)
    {
        _numberSize = size;
        ApplyNoteStyle(note => AnnotationStyler.WithNumberSize(note, size));
        UpdateStyleButtons();
    }

    /// <summary>水印文本：选中水印时实时改它，没选中时改下次创建的默认值。</summary>
    private void OnWatermarkBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingStyleControls)
            return;

        CommitWatermarkBox();
    }

    private void OnWatermarkBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitWatermarkBox();
        e.Handled = true;
    }

    private void OnWatermarkBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_updatingStyleControls)
            CommitWatermarkBox();
    }

    /// <summary>水印文本：选中水印时改它，没选中时改下次创建的默认值。</summary>
    internal void CommitWatermarkBox()
    {
        var text = WatermarkBox.Text ?? string.Empty;
        _watermarkText = text;

        ApplyNoteStyle(note => AnnotationStyler.WithWatermarkText(note, text), history: false);
    }

    private void OnNumberArabicMenu(object? sender, RoutedEventArgs e) => SetNumberFormat(NumberFormat.Arabic);
    private void OnNumberRomanMenu(object? sender, RoutedEventArgs e) => SetNumberFormat(NumberFormat.Roman);
    private void OnNumberLowerMenu(object? sender, RoutedEventArgs e) => SetNumberFormat(NumberFormat.LowerLetter);
    private void OnNumberUpperMenu(object? sender, RoutedEventArgs e) => SetNumberFormat(NumberFormat.UpperLetter);
    private void OnNumberChineseMenu(object? sender, RoutedEventArgs e) => SetNumberFormat(NumberFormat.Chinese);

    internal void SetNumberFormat(NumberFormat format)
    {
        _numberFormat = format;
        ApplyNoteStyle(note => AnnotationStyler.WithNumberFormat(note, format));
        UpdateStyleButtons();
    }

    private static string NumberFormatLabel(NumberFormat format) => format switch
    {
        NumberFormat.Roman => "罗马 Ⅰ Ⅱ Ⅲ",
        NumberFormat.LowerLetter => "小写 a b c",
        NumberFormat.UpperLetter => "大写 A B C",
        NumberFormat.Chinese => "中文 一 二 三",
        _ => "阿拉伯 1 2 3"
    };

    /// <summary>外形下拉：10 种徽章，Tag 里直接写枚举名。</summary>
    private void OnNumberShapeMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag }
            && Enum.TryParse<NumberShape>(tag, out var shape))
        {
            SetNumberShape(shape);
        }
    }

    internal void SetNumberShape(NumberShape shape)
    {
        _numberShape = shape;
        ApplyNoteStyle(note => AnnotationStyler.WithNumberShape(note, shape));
        UpdateStyleButtons();
    }

    private static string NumberShapeLabel(NumberShape shape) => shape switch
    {
        NumberShape.OutlineCircle => "描边圆",
        NumberShape.DoubleCircle => "双层圆",
        NumberShape.SolidSquare => "实心方块",
        NumberShape.RoundedSquare => "圆角方块",
        NumberShape.Capsule => "胶囊标签",
        NumberShape.Flag => "旗标",
        NumberShape.Pin => "定位针",
        NumberShape.Hexagon => "六边形",
        NumberShape.Corner => "角标标签",
        _ => "实心圆"
    };

    private void OnNumberComboMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag }
            && Enum.TryParse<NumberCombo>(tag, out var combo))
        {
            SetNumberCombo(combo);
        }
    }

    internal void SetNumberCombo(NumberCombo combo)
    {
        _numberCombo = combo;
        ApplyNoteStyle(note => AnnotationStyler.WithNumberCombo(note, combo));
        UpdateStyleButtons();
    }

    /// <summary>文本标签组合的说明文字：留空显示占位「说明」。</summary>
    private void OnNumberTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingStyleControls)
            return;

        CommitNumberText();
    }

    private void OnNumberTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitNumberText();
        e.Handled = true;
    }

    private void OnNumberTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_updatingStyleControls)
            CommitNumberText();
    }

    internal void CommitNumberText()
    {
        ApplyNoteStyle(note => AnnotationStyler.WithNumberText(note, NumberTextBox.Text));
    }

    private void OnNumberTextSize12Menu(object? sender, RoutedEventArgs e) => SetNumberTextSize(12);
    private void OnNumberTextSize14Menu(object? sender, RoutedEventArgs e) => SetNumberTextSize(14);
    private void OnNumberTextSize16Menu(object? sender, RoutedEventArgs e) => SetNumberTextSize(16);
    private void OnNumberTextSize20Menu(object? sender, RoutedEventArgs e) => SetNumberTextSize(20);
    private void OnNumberTextSize24Menu(object? sender, RoutedEventArgs e) => SetNumberTextSize(24);

    internal void SetNumberTextSize(double size)
    {
        _numberTextSize = size;
        ApplyNoteStyle(note => AnnotationStyler.WithNumberTextSize(note, size));
        UpdateStyleButtons();
    }

    private void OnNumberZoom2Menu(object? sender, RoutedEventArgs e) => SetNumberZoom(2);
    private void OnNumberZoom3Menu(object? sender, RoutedEventArgs e) => SetNumberZoom(3);
    private void OnNumberZoom4Menu(object? sender, RoutedEventArgs e) => SetNumberZoom(4);

    internal void SetNumberZoom(double zoom)
    {
        _numberZoom = zoom;
        ApplyNoteStyle(note => AnnotationStyler.WithNumberZoom(note, zoom));
        UpdateStyleButtons();
    }

    private static string NumberComboLabel(NumberCombo combo) => combo switch
    {
        NumberCombo.Leader => "引导线",
        NumberCombo.Arrow => "箭头",
        NumberCombo.Text => "文本标签",
        NumberCombo.DashedBox => "虚线框",
        NumberCombo.Highlight => "高亮区域",
        NumberCombo.Magnify => "放大说明",
        _ => "仅序号"
    };

    /// <summary>序号框显示的值：选中的序号，否则下一个自动号。</summary>
    private int EffectiveNumber =>
        _selectedNote >= 0 && _selectedNote < _notes.Items.Count
            && _notes.Items[_selectedNote] is NumberAnnotation number
            ? number.Number
            : _nextNumber;

    private string EffectiveLabel =>
        _selectedNote >= 0 && _selectedNote < _notes.Items.Count
            && _notes.Items[_selectedNote] is NumberAnnotation number
                && !string.IsNullOrWhiteSpace(number.Label)
            ? number.Label!
            : string.Empty;

    private void OnNumberBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitNumberBox();
        e.Handled = true;
    }

    private void OnNumberBoxLostFocus(object? sender, RoutedEventArgs e) => CommitNumberBox();

    /// <summary>序号：留空保持自动递增；选中序号时改它并顺延下一个，否则直接改下一个号。</summary>
    internal void CommitNumberBox()
    {
        var text = NumberBox.Text?.Trim() ?? string.Empty;

        if (!int.TryParse(text, out var value) || value < 0)
        {
            RefreshNumberInputs();
            return;
        }

        if (_selectedNote >= 0 && _selectedNote < _notes.Items.Count
            && _notes.Items[_selectedNote] is NumberAnnotation)
        {
            ApplyNoteStyle(note => AnnotationStyler.WithNumber(note, value));
            _nextNumber = value + 1;
        }
        else
        {
            _nextNumber = value;
        }

        UpdateStyleButtons();
    }

    private void OnLabelBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitLabelBox();
        e.Handled = true;
    }

    private void OnLabelBoxLostFocus(object? sender, RoutedEventArgs e) => CommitLabelBox();

    /// <summary>标签：留空显示自动序号，填了就用这段文字盖住序号。</summary>
    internal void CommitLabelBox()
    {
        var label = LabelBox.Text?.Trim();
        if (string.IsNullOrEmpty(label))
            label = null;

        ApplyNoteStyle(note => AnnotationStyler.WithNumberLabel(note, label));
        UpdateStyleButtons();
    }

    private void RefreshNumberInputs()
    {
        if (!NumberBox.IsFocused)
            NumberBox.Text = EffectiveNumber.ToString();

        if (!LabelBox.IsFocused)
            LabelBox.Text = EffectiveLabel;
    }

    private void OnMosaicPixelMenu(object? sender, RoutedEventArgs e) => ToggleMosaicBlur(false);
    private void OnMosaicBlurMenu(object? sender, RoutedEventArgs e) => ToggleMosaicBlur(true);

    /// <summary>切换马赛克像素 / 模糊；菜单和测试共用。</summary>
    internal void ToggleMosaicBlur(bool blur)
    {
        _mosaicBlur = blur;
        ApplyNoteStyle(note => AnnotationStyler.WithMosaic(note, _mosaicCell, blur));
        UpdateStyleButtons();
    }

    private void OnBrush8Menu(object? sender, RoutedEventArgs e) => SetMosaicCell(8);
    private void OnBrush12Menu(object? sender, RoutedEventArgs e) => SetMosaicCell(12);
    private void OnBrush20Menu(object? sender, RoutedEventArgs e) => SetMosaicCell(20);

    private void SetMosaicCell(int cell)
    {
        _mosaicCell = cell;
        ApplyNoteStyle(note => AnnotationStyler.WithMosaic(note, cell, _mosaicBlur));
        UpdateStyleButtons();
    }

    private void OnCornerRadiusClick(object? sender, RoutedEventArgs e)
    {
        _cornerRadius = _cornerRadius > 0 ? 0 : 10;
        UpdateStyleButtons();

        if (_hasSelection)
            UpdateSelectionVisuals();
    }

    private static readonly uint FillPresetPink = Color.Parse("#FFCCC7").ToUInt32();
    private static readonly uint FillPresetWhite = Color.Parse("#FFFFFF").ToUInt32();
    private static readonly uint FillPresetBlue = Color.Parse("#BAE0FF").ToUInt32();

    /// <summary>填充组选中态：样式按钮 + 预设色块，两处属性栏共用。</summary>
    private void UpdateFillButtons()
    {
        var fill = ActiveStyleTool == AnnotationTool.Bubble ? _bubbleFill : _noteFill;
        SetSelected(FillNoneButton, fill == ShapeFill.None);
        SetSelected(FillSoftButton, fill == ShapeFill.Soft);
        SetSelected(FillSolidButton, fill == ShapeFill.Solid);
        SetSelected(FillLineButton, fill == ShapeFill.Line);
        SetSelected(FillCrossButton, fill == ShapeFill.CrossLine);
        FillColorSwatch.Background = new SolidColorBrush(FillColorValue);

        var value = ActiveFillColorValue;
        var solid = fill == ShapeFill.Solid;
        SetSelected(FillPresetPinkButton, solid && value == FillPresetPink);
        SetSelected(FillPresetWhiteButton, solid && value == FillPresetWhite);
        SetSelected(FillPresetBlueButton, solid && value == FillPresetBlue);
    }

    /// <summary>
    /// 样式修改落地：优先改选中的标注；没选中时跟随「当前工具最近画的那条」，
    /// 所以画完立刻换外形/颜色/线型会马上生效；都没有才只改新建默认值（调用方负责）。
    /// </summary>
    private void ApplyNoteStyle(Func<IAnnotation, IAnnotation> transform, bool history = true)
    {
        var index = _selectedNote;

        if (index < 0)
        {
            var tool = ActiveStyleTool;

            for (var candidate = _notes.Items.Count - 1; candidate >= 0; candidate--)
            {
                if (AnnotationStyler.ToolOf(_notes.Items[candidate]) == tool)
                {
                    index = candidate;
                    break;
                }
            }
        }

        if (index < 0 || index >= _notes.Items.Count)
            return;

        var updated = transform(_notes.Items[index]);
        if (history)
            _notes.ReplaceWithHistory(index, updated);
        else
            _notes.Replace(index, updated);

        RebuildNotePreview();
    }

    private bool _updatingStyleControls;

    /// <summary>刷新属性栏控件；程序化赋值期间压住 slider 的 ValueChanged。</summary>
    private void UpdateStyleButtons()
    {
        _updatingStyleControls = true;
        RefreshStyleControls();
        _updatingStyleControls = false;
    }

    private void RefreshStyleControls()
    {
        // 形状类型（属性栏）与主条折叠组同步
        var active = ActiveStyleTool;
        SetSelected(ShapeRectButton, active == AnnotationTool.Rectangle);
        SetSelected(ShapeEllipseButton, active == AnnotationTool.Ellipse);
        SetSelected(ShapeDiamondButton, active == AnnotationTool.Diamond);
        SetSelected(ShapeBracketButton, active == AnnotationTool.Bracket);

        SetSelected(ColorAmberButton, IsColor("#E5A44C"));
        SetSelected(ColorWhiteButton, IsColor("#FFFFFF"));
        SetSelected(ColorRedButton, IsColor("#FF5F5F"));
        SetSelected(ColorGreenButton, IsColor("#5FD08C"));
        SetSelected(ColorBlueButton, IsColor("#5FA8FF"));

        SetSelected(Width2Button, _noteWidth < 2.5);
        SetSelected(Width3Button, _noteWidth >= 2.5 && _noteWidth < 3.5);
        SetSelected(Width4Button, _noteWidth >= 3.5 && _noteWidth < 6);
        SetSelected(Width8Button, _noteWidth >= 6 && _noteWidth < 10);
        SetSelected(Width12Button, _noteWidth >= 10);
        // 描边五个选项互斥：线型 + 笔触的组合对应其中一个
        SetSelected(DashSolidButton, _noteDash == LineDash.Solid && _noteStyle == LineStyle.Neat);
        SetSelected(DashDashButton, _noteDash == LineDash.Dash && _noteStyle == LineStyle.Neat);
        SetSelected(DashDotButton, _noteDash == LineDash.Dot && _noteStyle == LineStyle.Neat);
        SetSelected(StyleSketchButton,
            _noteDash == LineDash.Solid && _noteStyle == LineStyle.Sketch);
        SetSelected(StyleRoughButton,
            _noteDash == LineDash.Solid && _noteStyle == LineStyle.Rough);
        UpdateFillButtons();

        var opacity = active switch
        {
            AnnotationTool.Spotlight => _spotlightOpacity,
            AnnotationTool.Watermark => _watermarkOpacity,
            _ => _noteOpacity
        };
        OpacitySlider.Value = Math.Round(opacity * 100);
        OpacityValue.Text = $"{opacity * 100:0}%";
        RadiusSlider.Value = _noteRadius;
        RadiusValue.Text = $"{_noteRadius:0} px";

        // 字号用 S/M/L/XL 档位；不落在档位上时四个都不亮
        var fontSize = ActiveFontSize;
        SetSelected(FontSButton, Math.Abs(fontSize - 14) < 0.1);
        SetSelected(FontMButton, Math.Abs(fontSize - 16) < 0.1);
        SetSelected(FontLButton, Math.Abs(fontSize - 20) < 0.1);
        SetSelected(FontXlButton, Math.Abs(fontSize - 32) < 0.1);
        SetSelected(BoldButton, _noteBold);
        SetSelected(ItalicButton, _noteItalic);
        SetSelected(AlignLeftButton, _noteAlign == TextAlign.Left);
        SetSelected(AlignCenterButton, _noteAlign == TextAlign.Center);
        SetSelected(AlignRightButton, _noteAlign == TextAlign.Right);
        BackgroundMenuButton.Content = BackgroundLabel(_textBackground);
        StrokeWidthMenuButton.Content =
            _textStrokeWidth <= 0 ? "无描边" : $"{_textStrokeWidth:0} px";
        StrokeColorMenuButton.Content = StrokeColorLabel(_textStrokeColor);

        if (_noteFontFamily.Length > 0
            && FontFamilyBox.SelectedItem as string != _noteFontFamily)
        {
            FontFamilyBox.SelectedItem = _noteFontFamily;
        }
        NumberSizeMenuButton.Content = $"{_numberSize:0} px";
        NumberFormatButton.Content = NumberFormatLabel(_numberFormat);
        NumberShapeButton.Content = NumberShapeLabel(_numberShape);
        NumberComboButton.Content = NumberComboLabel(_numberCombo);
        NumberTextGroup.IsVisible = _numberCombo == NumberCombo.Text;
        NumberZoomGroup.IsVisible = _numberCombo == NumberCombo.Magnify;
        NumberTextSizeButton.Content = $"{_numberTextSize:0} px";
        NumberZoomButton.Content = $"{_numberZoom:0.#}×";

        if (!NumberTextBox.IsFocused)
        {
            var numberNote = _selectedNote >= 0 && _selectedNote < _notes.Items.Count
                ? _notes.Items[_selectedNote] as NumberAnnotation
                : null;
            NumberTextBox.Text = numberNote?.Text ?? string.Empty;
        }
        RefreshNumberInputs();

        if (!WatermarkBox.IsFocused)
            WatermarkBox.Text = _watermarkText;

        SetSelected(MosaicPixelButton, !_mosaicBlur);
        SetSelected(MosaicBlurButton, _mosaicBlur);
        BrushMenuButton.Content = $"{_mosaicCell} px";

        SetSelected(CornerRadiusButton, _cornerRadius > 0);
    }

    private bool IsColor(string hex) => _noteColor.ToUInt32() == Color.Parse(hex).ToUInt32();

    /// <summary>新的标注动作会截断「选区移动」的撤销/重做链，保持严格 LIFO。</summary>
    private void MarkNoteMutation()
    {
        _selectionUndo.Clear();
        _selectionRedo.Clear();
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e) => UndoAnnotation();

    private void UndoAnnotation()
    {
        if (_selectionUndo.Count > 0)
        {
            var snapshot = _selectionUndo.Pop();
            _selectionRedo.Push(new SelectionSnapshot(_selection, _notes.Items.ToList()));
            RestoreSelectionSnapshot(snapshot);
            return;
        }

        _notes.Undo();
        _selectedNote = -1;
        RebuildNotePreview();
        UpdateToolButtons();
    }

    private void OnRedoClick(object? sender, RoutedEventArgs e) => RedoAnnotation();

    private void RedoAnnotation()
    {
        if (_selectionRedo.Count > 0)
        {
            var snapshot = _selectionRedo.Pop();
            _selectionUndo.Push(new SelectionSnapshot(_selection, _notes.Items.ToList()));
            RestoreSelectionSnapshot(snapshot);
            return;
        }

        _notes.Redo();
        _selectedNote = -1;
        RebuildNotePreview();
        UpdateToolButtons();
    }

    /// <summary>恢复一次选区移动/拉伸：选区几何和标注一起回退。</summary>
    private void RestoreSelectionSnapshot(SelectionSnapshot snapshot)
    {
        HideTextEditor();

        _selection = snapshot.Selection;
        _selectionOrigin = _selection;
        _hasSelection = true;
        _selectedNote = -1;
        _draggingNote = -1;
        _notesBeforeDrag = null;
        _notes.SetItems(snapshot.Notes);

        UpdateSelectionVisuals();
        UpdateHandles();
        RebuildNotePreview();
        UpdateToolButtons();
    }

    // ---------- 标注的选中 / 拖动编辑 ----------

    /// <summary>选中一条标注（-1 = 取消选中），选中态画虚线外框。</summary>
    internal void SelectNote(int index)
    {
        if (_selectedNote == index)
            return;

        _selectedNote = index >= 0 && index < _notes.Items.Count ? index : -1;

        if (_selectedNote >= 0)
            SyncStyleFromNote(_notes.Items[_selectedNote]);

        RebuildNotePreview();
        UpdateToolButtons();
    }

    /// <summary>选中标注时把它的样式回填到属性栏；同时作为下次新建的默认值。</summary>
    private void SyncStyleFromNote(IAnnotation note)
    {
        switch (note)
        {
            case ShapeAnnotation s:
                ApplyLineStyle(s.Color, s.Width, s.Opacity, s.Dash, s.Style);
                _noteFill = s.Fill;
                _noteFillColor = s.FillColor;
                _noteRadius = s.Radius;
                break;

            case ArrowAnnotation a:
                ApplyLineStyle(a.Color, a.Width, a.Opacity, a.Dash, a.Style);
                break;

            case LineAnnotation l:
                ApplyLineStyle(l.Color, l.Width, l.Opacity, l.Dash, l.Style);
                break;

            case DistanceAnnotation d:
                ApplyLineStyle(d.Color, d.Width, d.Opacity, d.Dash, d.Style);
                break;

            case BracketAnnotation b:
                ApplyLineStyle(b.Color, b.Width, b.Opacity, b.Dash, b.Style);
                break;

            case PenAnnotation p:
                ApplyLineStyle(p.Color, p.Width, p.Opacity, p.Dash, p.Style);
                break;

            case AngleAnnotation g:
                _noteColor = Color.FromUInt32(g.Color);
                _noteWidth = g.Width;
                _noteOpacity = g.Opacity;
                break;

            case SpeechBubbleAnnotation b:
                ApplyLineStyle(b.Color, b.Width, b.Opacity, LineDash.Solid, LineStyle.Neat);
                _bubbleFill = b.Fill;
                _bubbleFillColor = b.FillColor;
                _bubbleFontSize = b.FontSize;
                break;

            case TextAnnotation t:
                _noteColor = Color.FromUInt32(t.Color);
                _noteFontSize = t.FontSize;
                _noteBold = t.Bold;
                _noteItalic = t.Italic;
                _noteFontFamily = t.FontFamily;
                _noteAlign = t.Align;
                _textBackground = t.BackgroundColor;
                _textStrokeColor = t.StrokeColor == 0 ? 0xFF000000 : t.StrokeColor;
                _textStrokeWidth = t.StrokeWidth;
                break;

            case NumberAnnotation n:
                _noteColor = Color.FromUInt32(n.Color);
                _numberSize = n.Size;
                _numberFormat = n.Format;
                _numberShape = n.Shape;
                _numberCombo = n.Combo;
                _numberTextSize = n.TextSize;
                _numberZoom = n.Zoom;
                break;

            case WatermarkAnnotation w:
                _noteColor = Color.FromUInt32(w.Color);
                _watermarkFontSize = w.FontSize;
                _watermarkOpacity = w.Opacity;
                break;

            case MosaicAnnotation m:
                _mosaicCell = m.Cell;
                _mosaicBlur = m.Blur;
                break;

            case SpotlightAnnotation s:
                _noteColor = Color.FromUInt32(s.Color);
                _spotlightOpacity = s.Opacity;
                break;
        }
    }

    private void ApplyLineStyle(uint color, double width, double opacity, LineDash dash, LineStyle style)
    {
        _noteColor = Color.FromUInt32(color);
        _noteWidth = width;
        _noteOpacity = opacity;
        _noteDash = dash;
        _noteStyle = style;
    }
}
