// CaptureWindow 分部类：键盘快捷键、取色采样与选区尺寸角标。
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
    private readonly record struct CaptureShortcut(Key Key, KeyModifiers Modifiers)
    {
        public bool Matches(Key key, KeyModifiers modifiers) =>
            Key == key && Modifiers == modifiers;
    }

    /// <summary>宿主注入的截图窗口快捷键（空字符串 = 禁用）。</summary>
    internal readonly record struct CaptureShortcutConfig(
        string? Recognize,
        string? Translate,
        string? Copy,
        string? Save,
        string? Pin,
        string? Scroll,
        string? Color,
        string? OcrBox);

    private CaptureShortcut? _recognizeShortcut = ParseShortcut("R");
    private CaptureShortcut? _translateShortcut = ParseShortcut("T");
    private CaptureShortcut? _copyShortcut = ParseShortcut("C");
    private CaptureShortcut? _saveShortcut = ParseShortcut("S");
    private CaptureShortcut? _pinShortcut = ParseShortcut("P");
    private CaptureShortcut? _scrollShortcut = ParseShortcut("G");
    private CaptureShortcut? _colorShortcut = ParseShortcut("I");
    private CaptureShortcut? _ocrBoxShortcut = ParseShortcut("B");

    /// <summary>宿主注入截图窗口内的动作键；空字符串表示禁用该快捷键。</summary>
    internal void ConfigureCaptureShortcuts(CaptureShortcutConfig config)
    {
        _recognizeShortcut = ParseShortcut(config.Recognize);
        _translateShortcut = ParseShortcut(config.Translate);
        _copyShortcut = ParseShortcut(config.Copy);
        _saveShortcut = ParseShortcut(config.Save);
        _pinShortcut = ParseShortcut(config.Pin);
        _scrollShortcut = ParseShortcut(config.Scroll);
        _colorShortcut = ParseShortcut(config.Color);
        _ocrBoxShortcut = ParseShortcut(config.OcrBox);
        UpdateCaptureShortcutTips();
    }

    private static CaptureShortcut? ParseShortcut(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !HotkeyText.TryParse(text, out var modifiers, out var virtualKey))
            return null;

        var key = ToKey(virtualKey);
        return key == Key.None ? null : new CaptureShortcut(key, ToKeyModifiers(modifiers));
    }

    private static Key ToKey(uint virtualKey) =>
        virtualKey switch
        {
            >= 'A' and <= 'Z' => Enum.TryParse<Key>(((char)virtualKey).ToString(), out var letter)
                ? letter
                : Key.None,
            >= '0' and <= '9' => (Key)((int)Key.D0 + (int)(virtualKey - '0')),
            _ => Key.None
        };

    /// <summary>Win32 MOD_* → Avalonia 修饰键。</summary>
    private static KeyModifiers ToKeyModifiers(uint modifiers)
    {
        var result = KeyModifiers.None;

        if ((modifiers & 0x0002) != 0)   // MOD_CONTROL
            result |= KeyModifiers.Control;

        if ((modifiers & 0x0001) != 0)   // MOD_ALT
            result |= KeyModifiers.Alt;

        if ((modifiers & 0x0004) != 0)   // MOD_SHIFT
            result |= KeyModifiers.Shift;

        if ((modifiers & 0x0008) != 0)   // MOD_WIN
            result |= KeyModifiers.Meta;

        return result;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 中文输入法会把字母键标记成 ImeProcessed；
        // 截图窗口没有文本输入，按物理键还原即可，不需要放行给输入法。
        var key = e.Key == Key.ImeProcessed ? MapPhysicalKey(e.PhysicalKey) : e.Key;

        if (!ShouldHandleGlobalKey(key))
        {
            base.OnKeyDown(e);
            return;
        }

        if (HandleNudgeKey(e, key)
            || HandleColorKey(e, key)
            || HandleHistoryKey(e, key)
            || HandleViewKey(e, key)
            || HandleToolKey(e, key)
            || HandleActionKey(e, key))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>焦点在文本 / 按钮 / 滑块上时，把按键让给控件自己。</summary>
    private bool ShouldHandleGlobalKey(Key key)
    {
        var focused = FocusManager?.GetFocusedElement();

        if (focused is TextBox)
            return false;

        if (focused is Button && key is Key.Enter or Key.Space)
            return false;

        if (focused is Slider && key is Key.Left or Key.Right or Key.Up or Key.Down)
            return false;

        return true;
    }

    /// <summary>方向键微调选区：1px；Shift + 方向键 10px。</summary>
    private bool HandleNudgeKey(KeyEventArgs e, Key key)
    {
        if (!_hasSelection
            || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)
            || key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return false;

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var dx = key switch
        {
            Key.Left => -step,
            Key.Right => step,
            _ => 0
        };
        var dy = key switch
        {
            Key.Up => -step,
            Key.Down => step,
            _ => 0
        };

        NudgeSelection(dx, dy);
        return true;
    }

    /// <summary>I：取色；取色中 Shift+I：切换格式（HEX / RGB / HSL）。</summary>
    private bool HandleColorKey(KeyEventArgs e, Key key)
    {
        // Shift+I：切换颜色格式（HEX / RGB / HSL）
        if (_colorSampling && key == Key.I && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _colorFormat = (_colorFormat + 1) % 3;
            UpdateColorProbe(_lastPointerPoint);
            return true;
        }

        if (_colorShortcut?.Matches(key, e.KeyModifiers) != true)
            return false;

        // 取色器常驻跟随，按 I 直接复制当前颜色
        CommitColorSample();
        return true;
    }

    /// <summary>撤销 / 重做 / 删除选中标注。</summary>
    private bool HandleHistoryKey(KeyEventArgs e, Key key)
    {
        if (key == Key.Z
            && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            RedoAnnotation();
            return true;
        }

        if (key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            RedoAnnotation();
            return true;
        }

        if (key is Key.Delete or Key.Back && _selectedNote >= 0)
        {
            DeleteSelectedNote();
            return true;
        }

        if (key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            UndoAnnotation();
            return true;
        }

        return false;
    }

    /// <summary>Tab 切换识别层级、B 切换识别框、Esc 取消。</summary>
    private bool HandleViewKey(KeyEventArgs e, Key key)
    {
        if (key == Key.Tab && HandleTabKey(e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            return true;

        if (_ocrBoxShortcut?.Matches(key, e.KeyModifiers) == true && OcrLayer.IsVisible)
        {
            CycleOcrBoxMode();
            return true;
        }

        return key == Key.Escape && HandleEscape();
    }

    /// <summary>标注工具热键：数字、字母映射、Shift+B 括号、Shift+G 角度。</summary>
    private bool HandleToolKey(KeyEventArgs e, Key key)
    {
        if (!_hasSelection)
            return false;

        if (e.KeyModifiers == KeyModifiers.None && key is Key.D1 or Key.NumPad1)
        {
            _tool = AnnotationTool.None;
            UpdateToolButtons();
            return true;
        }

        if (e.KeyModifiers == KeyModifiers.None && TryMapToolKey(key, out var tool))
        {
            SetTool(tool);
            return true;
        }

        // Shift+B：括号；`[` 在部分键盘布局下取不到，留一个别名
        if (key == Key.B && e.KeyModifiers == KeyModifiers.Shift)
        {
            SetTool(AnnotationTool.Bracket);
            return true;
        }

        // Shift+G：角度；G 单独留给滚动截图
        if (key == Key.G && e.KeyModifiers == KeyModifiers.Shift)
        {
            SetTool(AnnotationTool.Angle);
            return true;
        }

        return false;
    }

    /// <summary>动作栏快捷键：复制文本、识别、保存/贴图等。</summary>
    private bool HandleActionKey(KeyEventArgs e, Key key)
    {
        // 有识别结果时，Ctrl+C 复制全文；单击 C 仍然是复制图片
        if (ActionBar.IsVisible
            && OcrLayer.IsVisible
            && key == Key.C
            && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopyAllText();
            return true;
        }

        if (!ActionBar.IsVisible)
            return false;

        // Enter 保留为「识别 / 复制全文」的固定键，其余动作走可配置快捷键
        if (key == Key.Enter)
        {
            if (OcrLayer.IsVisible)
                CopyAllText();
            else if (OcrRunner is not null)
                _ = RunOcrAsync();
            else
                RunActionShortcut(CaptureAction.Recognize);

            return true;
        }

        if (_recognizeShortcut?.Matches(key, e.KeyModifiers) == true)
        {
            RunActionShortcut(CaptureAction.Recognize);
            return true;
        }

        if (_translateShortcut?.Matches(key, e.KeyModifiers) == true)
        {
            _ = TranslateFromShortcutAsync();
            return true;
        }

        if (_copyShortcut?.Matches(key, e.KeyModifiers) == true)
        {
            RunActionShortcut(CaptureAction.CopyImage);
            return true;
        }

        if (_saveShortcut?.Matches(key, e.KeyModifiers) == true)
        {
            RunActionShortcut(CaptureAction.Save);
            return true;
        }

        if (_pinShortcut?.Matches(key, e.KeyModifiers) == true)
        {
            RunActionShortcut(CaptureAction.Pin);
            return true;
        }

        if (_scrollShortcut?.Matches(key, e.KeyModifiers) == true)
        {
            RunActionShortcut(CaptureAction.Scroll);
            return true;
        }

        return false;
    }

    /// <summary>没有就地 OCR 时保持旧行为：设置动作后关窗交给宿主。</summary>
    private void RunActionShortcut(CaptureAction action)
    {
        if (OcrRunner is null)
        {
            Action = action;
            Close();
            return;
        }

        _ = ActAsync(action);
    }

    private async Task TranslateFromShortcutAsync()
    {
        if (!string.IsNullOrEmpty(_translateTargetSelection))
            await RunTranslateAsync(_translateTargetSelection);
    }

    /// <summary>把按钮提示同步成当前配置的快捷键。</summary>
    private void UpdateCaptureShortcutTips()
    {
        ToolTip.SetTip(
            RecognizeButton,
            $"识别选区文字（{ShortcutLabel(_recognizeShortcut)} / Enter）");
        ToolTip.SetTip(
            TranslateActionButton,
            $"识别并翻译（{ShortcutLabel(_translateShortcut)}）");
        ToolTip.SetTip(
            SaveButton,
            $"保存为 PNG（{ShortcutLabel(_saveShortcut)}）");
        ToolTip.SetTip(
            PinButton,
            $"固定到屏幕（{ShortcutLabel(_pinShortcut)}）");
        ToolTip.SetTip(
            ScrollButton,
            $"滚动截图（{ShortcutLabel(_scrollShortcut)}）");
        ToolTip.SetTip(
            CopyImageButton,
            $"完成并复制（{ShortcutLabel(_copyShortcut)}）");
    }

    private static string ShortcutLabel(CaptureShortcut? shortcut) =>
        shortcut is { } value
            ? $"{DescribeModifiers(value.Modifiers)}{value.Key}"
            : "未设置";

    private static string DescribeModifiers(KeyModifiers modifiers)
    {
        var parts = new List<string>();

        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");

        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");

        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");

        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Win");

        return parts.Count == 0 ? string.Empty : string.Join("+", parts) + "+";
    }
    /// <summary>物理键 → 逻辑键：字母/数字键在 ImeProcessed 下仍能匹配可配置快捷键。</summary>
    private static Key MapPhysicalKey(PhysicalKey physicalKey) =>
        Enum.TryParse<Key>(physicalKey.ToString(), out var key) ? key : Key.None;

    // ---------- 截图内取色 ----------

    /// <summary>是否处于取色模式（测试和宿主都可以查）。</summary>
    internal bool ColorSampling => _colorSampling;

    /// <summary>进入 / 退出取色模式（I 键和右键共用）。</summary>
    internal void ToggleColorSampling()
    {
        if (_colorSampling)
        {
            ExitColorSampling();
            return;
        }

        _colorSampling = true;
        _colorFormat = 0;
        Magnifier.IsVisible = false;
        Cursor = CrossCursor;

        // 用当前真实光标位置初始化：刚打开窗口还没收到 PointerMoved 时，
        // _lastPointerPoint 仍是 (0,0)，取色器会停在左上角被提示条挡住。
        var screenCursor = WindowCandidates.GetCursorPosition();
        var scale = _scaling > 0 ? _scaling : 1;
        var cursor = new Point(
            (screenCursor.X - Position.X) / scale,
            (screenCursor.Y - Position.Y) / scale);

        if (cursor.X < 0 || cursor.Y < 0 || cursor.X > Width || cursor.Y > Height)
            cursor = new Point(Width / 2, Height / 2);

        _lastPointerPoint = cursor;
        UpdateColorProbe(_lastPointerPoint);
    }

    internal void ExitColorSampling()
    {
        _colorSampling = false;
        ColorProbe.IsVisible = false;
        Magnifier.IsVisible = false;
        UpdateCursor(_lastPointerPoint);
    }

    /// <summary>有选区（或当前识别到的控件区域）时自动显示取色器，不需要先按 I。</summary>
    private void UpdateAutoColorProbe(Point point)
    {
        var inArea = _hasSelection && _selection.Contains(point);

        // 自动识别控件 / 窗口：还没有选区时，按当前候选高亮区域取色
        if (!inArea && !_hasSelection && _currentCandidate is { } candidate)
            inArea = ToDip(candidate.Bounds).Contains(point);

        if (!_colorSampling || !inArea)
        {
            ColorProbe.IsVisible = false;
            return;
        }

        UpdateColorProbe(point);
    }

    /// <summary>把光标处的颜色和坐标写进取色卡片，并跟着光标摆位。</summary>
    private void UpdateColorProbe(Point point)
    {
        if (SampleColorAt(point) is not { } color)
        {
            // 冻结帧还没准备好：先给反馈，避免看起来没反应
              ProbeSwatch.Background = Brushes.Transparent;
            ProbeValue.Text = "准备中…";
            ProbePosition.Text = $"{point.X:0}, {point.Y:0}";
            ColorProbe.IsVisible = true;
            PositionColorProbe(point);
            return;
        }

        ProbeSwatch.Background = new SolidColorBrush(color);
        ProbeValue.Text = FormatColor(color, _colorFormat);
        ProbePosition.Text = $"{point.X:0}, {point.Y:0}";
        ColorProbe.IsVisible = true;
        UpdateProbePreview(point);
        PositionColorProbe(point);
    }

    /// <summary>方形像素预览：最近邻放大，光标所在像素固定落在预览中心。</summary>
    private void UpdateProbePreview(Point point)
    {
        if (_frozenBitmap is null)
            return;

        ProbePreviewImage.Source = _frozenBitmap;
        ProbePreviewImage.Width = Math.Max(Width, 1);
        ProbePreviewImage.Height = Math.Max(Height, 1);

        if (ProbePreviewImage.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = ColorPickZoom;
            scale.ScaleY = ColorPickZoom;
        }

        // 预览区 108px；让光标像素落在 54px 中心
        Canvas.SetLeft(ProbePreviewImage, -(point.X * ColorPickZoom) + 54);
        Canvas.SetTop(ProbePreviewImage, -(point.Y * ColorPickZoom) + 54);
    }

    /// <summary>取色卡片贴着光标右下，靠边时钳制在窗口内。</summary>
    private void PositionColorProbe(Point point)
    {
        ColorProbe.Measure(Size.Infinity);
        var size = ColorProbe.DesiredSize;
        var left = Math.Clamp(point.X + 24, 4, Math.Max(4, Width - size.Width - 4));
        var top = Math.Clamp(point.Y + 24, 4, Math.Max(4, Height - size.Height - 4));

        Canvas.SetLeft(ColorProbe, left);
        Canvas.SetTop(ColorProbe, top);
    }

    /// <summary>复制当前光标颜色；取色器本身常驻，不需要进入/退出模式。</summary>
    private void CommitColorSample()
    {
        if (SampleColorAt(_lastPointerPoint) is { } color)
        {
            CopyText?.Invoke(FormatColor(color, _colorFormat));
            _noteColor = color;
            UpdateStyleButtons();
            ProbeValue.Text = $"{FormatColor(color, _colorFormat)} 已复制";
        }
    }

    /// <summary>
    /// 从冻结帧里读一个像素。窗口是 DIP、冻结帧是物理像素，按比例换算。
    /// 没有冻结帧（比如刚打开还没抓到）时返回 null。
    /// </summary>
    internal Color? SampleColorAt(Point point)
    {
        if (_frozenPixels is null || _frozenWidth <= 0 || _frozenHeight <= 0 ||
            Width <= 0 || Height <= 0)
            return null;

        var x = (int)Math.Round(point.X * _frozenWidth / Width);
        var y = (int)Math.Round(point.Y * _frozenHeight / Height);

        if (x < 0 || y < 0 || x >= _frozenWidth || y >= _frozenHeight)
            return null;

        var offset = (y * _frozenWidth + x) * 4;
        if (offset + 3 >= _frozenPixels.Length)
            return null;

        // 冻结帧是 BGRA
        return Color.FromArgb(255, _frozenPixels[offset + 2], _frozenPixels[offset + 1],
            _frozenPixels[offset]);
    }

    /// <summary>0 = HEX，1 = RGB，2 = HSL。</summary>
    internal static string FormatColor(Color color, int format)
    {
        switch (format)
        {
            case 1:
                return $"rgb({color.R}, {color.G}, {color.B})";
            case 2:
            {
                var (h, s, l) = ToHsl(color);
                return $"hsl({h:0}, {s:0}%, {l:0}%)";
            }
            default:
                return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
    }

    private static (double h, double s, double l) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;

        if (d < 1e-6)
            return (0, 0, l * 100);

        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (r >= g && r >= b)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (g >= b)
            h = (b - r) / d + 2;
        else
            h = (r - g) / d + 4;

        return (h * 60, s * 100, l * 100);
    }

    /// <summary>测试用：直接灌入一帧像素，避免依赖真实抓屏。</summary>
    internal void SetFrozenPixelsForTest(byte[] pixels, int width, int height)
    {
        _frozenPixels = pixels;
        _frozenWidth = width;
        _frozenHeight = height;
        _frozenBitmap = CreateBitmap(pixels, width, height);
    }

    /// <summary>尺寸浮条的落点：优先选区上方，顶到边缘时放进内侧；工具条避让也用它。</summary>
    private (Size Size, Point Position) ComputeSizeBadgePlacement(Rect rect)
    {
        SizeBadge.Measure(Size.Infinity);
        var size = SizeBadge.DesiredSize;
        const double gap = 8;
        const double margin = 4;

        var screen = ScreenDipFor(rect) ?? new Rect(0, 0, Width, Height);

        // 候选顺序：上 → 右 → 左 → 下（参考 snow-shot 的 selectionToolbarContentPosition），
        // 取第一个完全落在屏幕内的位置，贴顶时不再压进选区里
        var clampedX = Math.Clamp(
            rect.X,
            screen.X + margin,
            Math.Max(screen.X + margin, screen.Right - size.Width - margin));

        var candidates = new[]
        {
            new Point(clampedX, rect.Y - size.Height - gap),
            new Point(rect.Right + gap, rect.Y),
            new Point(rect.X - size.Width - gap, rect.Y),
            new Point(clampedX, rect.Bottom + gap),
        };

        foreach (var candidate in candidates)
        {
            var area = new Rect(candidate, size);
            if (area.Left >= screen.X + margin && area.Top >= screen.Y + margin
                && area.Right <= screen.Right - margin && area.Bottom <= screen.Bottom - margin)
                return (size, candidate);
        }

        // 都放不下：回到上方再整体钳制，保证确定性
        var fallback = new Point(
            clampedX,
            Math.Clamp(
                rect.Y - size.Height - gap,
                screen.Y + margin,
                Math.Max(screen.Y + margin, screen.Bottom - size.Height - margin)));

        return (size, fallback);
    }

    /// <summary>
    /// 选区读数：左上角物理坐标 + 物理像素尺寸，和最终截图一致。
    /// 默认放在选区上方；顶到屏幕边缘时放到下方。
    /// </summary>
    private void UpdateSizeBadge(Rect rect)
    {
        // 录制/启动录制期间隐藏读数，避免出现在视频里。
        if (_recording is not null || _startingRecording)
        {
            SizeBadge.IsVisible = false;
            return;
        }

        if (rect.Width < 1 || rect.Height < 1)
        {
            SizeBadge.IsVisible = false;
            return;
        }

        SizeBadge.IsVisible = true;

        var unit = _sizeInPixels ? _scaling : 1.0;

        // 回填读数：拖动 / 拉伸选区时跟着刷新，但不触发提交
        _sizeEditGuard = true;
        PosXBox.Text = ((int)Math.Round(rect.X * unit)).ToString();
        PosYBox.Text = ((int)Math.Round(rect.Y * unit)).ToString();
        WidthBox.Text = ((int)Math.Round(rect.Width * unit)).ToString();
        HeightBox.Text = ((int)Math.Round(rect.Height * unit)).ToString();
        RadiusBox.Text = ((int)Math.Round(_cornerRadius)).ToString();
        SizeUnitButton.Content = _sizeInPixels ? "px" : "DIP";
        _sizeEditGuard = false;

        var (_, position) = ComputeSizeBadgePlacement(rect);
        Canvas.SetLeft(SizeBadge, position.X);
        Canvas.SetTop(SizeBadge, position.Y);
    }
}
