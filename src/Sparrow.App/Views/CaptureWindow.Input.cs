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
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 中文输入法会把字母键标记成 ImeProcessed；
        // 截图窗口没有文本输入，按物理键还原即可，不需要放行给输入法。
        var key = e.Key == Key.ImeProcessed ? MapPhysicalKey(e.PhysicalKey) : e.Key;

        // 焦点停在输入框（尺寸 / 文字）时，按键交给输入框，
        // 否则在尺寸框里输入数字会被当成切换工具
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        // 焦点在按钮上时，Enter / Space 交给按钮自己触发，避免被全局快捷键抢走
        if (FocusManager?.GetFocusedElement() is Button
            && key is Key.Enter or Key.Space)
        {
            base.OnKeyDown(e);
            return;
        }

        // 焦点在滑块上时，方向键交给滑块
        if (FocusManager?.GetFocusedElement() is Slider
            && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            base.OnKeyDown(e);
            return;
        }

        // 方向键微调选区：1px；Shift + 方向键 10px
        if (_hasSelection
            && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift
            && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
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
            e.Handled = true;
            return;
        }

        // I：取色；取色中 Shift+I：切换格式（HEX / RGB / HSL）
        if (key == Key.I && _colorSampling && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _colorFormat = (_colorFormat + 1) % 3;
            UpdateColorProbe(_lastPointerPoint);
            e.Handled = true;
            return;
        }

        if (key == Key.I && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleColorSampling();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Z / Ctrl+Y：重做；Ctrl+Z：撤销
        if (key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            RedoAnnotation();
            e.Handled = true;
            return;
        }

        if (key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            RedoAnnotation();
            e.Handled = true;
            return;
        }

        // Delete / Backspace：删掉当前选中的那条标注
        if (key is Key.Delete or Key.Back && _selectedNote >= 0)
        {
            DeleteSelectedNote();
            e.Handled = true;
            return;
        }

        if (key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            UndoAnnotation();
            e.Handled = true;
            return;
        }

        // 数字键 1 回到指针；2..9 切标注工具（和按钮提示一致）
        if (e.KeyModifiers == KeyModifiers.None && _hasSelection && (key is Key.D1 or Key.NumPad1))
        {
            _tool = AnnotationTool.None;
            UpdateToolButtons();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && TryMapToolKey(key, out var tool)
            && _hasSelection)
        {
            SetTool(tool);
            e.Handled = true;
            return;
        }

        // Tab：整窗 ↔ 子元素；Ctrl+Tab：在光标下重叠的窗口之间循环
        if (key == Key.Tab && HandleTabKey(e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            e.Handled = true;
            return;
        }

        // B：切换识别框显示方式（全部 / 悬停 / 关闭）
        if (key == Key.B && OcrLayer.IsVisible)
        {
            CycleOcrBoxMode();
            e.Handled = true;
            return;
        }

        // Shift+B：括号；`[` 在部分键盘布局下取不到，留一个别名
        if (key == Key.B && e.KeyModifiers == KeyModifiers.Shift && _hasSelection)
        {
            SetTool(AnnotationTool.Bracket);
            e.Handled = true;
            return;
        }

        // Shift+G：角度；G 单独留给滚动截图
        if (key == Key.G && e.KeyModifiers == KeyModifiers.Shift && _hasSelection)
        {
            SetTool(AnnotationTool.Angle);
            e.Handled = true;
            return;
        }

        if (key == Key.Escape && HandleEscape())
        {
            e.Handled = true;
            return;
        }

        // 有识别结果时，Ctrl+C 复制全文；单击 C 仍然是复制图片
        if (ActionBar.IsVisible
            && OcrLayer.IsVisible
            && key == Key.C
            && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CopyAllText();
            e.Handled = true;
            return;
        }

        // 注入了 OCR runner 时，Enter/R 在窗口内就地识别
        if (ActionBar.IsVisible && OcrRunner is not null && key is Key.Enter or Key.R)
        {
            if (key == Key.Enter && OcrLayer.IsVisible)
                CopyAllText();
            else
                _ = RunOcrAsync();

            e.Handled = true;
            return;
        }

        // 动作栏出现后，整条流程都可以只用键盘完成
        if (ActionBar.IsVisible && TryResolveShortcut(key, out var action))
        {
            if (OcrRunner is null)
            {
                // 没有就地 OCR：保持旧行为，关窗交给宿主
                Action = action;
                Close();
            }
            else
            {
                _ = ActAsync(action);
            }

            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static Key MapPhysicalKey(PhysicalKey physicalKey) => physicalKey switch
    {
        PhysicalKey.Enter => Key.Enter,
        PhysicalKey.R => Key.R,
        PhysicalKey.C => Key.C,
        PhysicalKey.S => Key.S,
        PhysicalKey.P => Key.P,
        PhysicalKey.G => Key.G,
        _ => Key.None
    };

    private static bool TryResolveShortcut(Key key, out CaptureAction action)
    {
        switch (key)
        {
            case Key.Enter:
            case Key.R:
                action = CaptureAction.Recognize;
                return true;
            case Key.C:
                action = CaptureAction.CopyImage;
                return true;
            case Key.S:
                action = CaptureAction.Save;
                return true;
            case Key.P:
                action = CaptureAction.Pin;
                return true;
            case Key.G:
                action = CaptureAction.Scroll;
                return true;
            default:
                action = CaptureAction.None;
                return false;
        }
    }

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
        UpdateColorProbe(_lastPointerPoint);
    }

    internal void ExitColorSampling()
    {
        _colorSampling = false;
        ColorProbe.IsVisible = false;
        UpdateCursor(_lastPointerPoint);
    }

    /// <summary>把光标处的颜色和坐标写进探针，并跟着光标摆位。</summary>
    private void UpdateColorProbe(Point point)
    {
        if (!_colorSampling)
        {
            ColorProbe.IsVisible = false;
            return;
        }

        if (SampleColorAt(point) is not { } color)
        {
            ColorProbe.IsVisible = false;
            return;
        }

        ProbeSwatch.Background = new SolidColorBrush(color);
        ProbeValue.Text = FormatColor(color, _colorFormat);
        ProbePosition.Text = $"{point.X:0}, {point.Y:0}";
        ColorProbe.IsVisible = true;

        ColorProbe.Measure(Size.Infinity);
        var size = ColorProbe.DesiredSize;
        var left = Math.Max(4, Math.Min(point.X + 22, Width - size.Width - 4));
        var top = Math.Max(4, Math.Min(point.Y + 24, Height - size.Height - 4));
        Canvas.SetLeft(ColorProbe, left);
        Canvas.SetTop(ColorProbe, top);
    }

    /// <summary>点一下：复制当前颜色、写回标注颜色，然后退出取色。</summary>
    private void CommitColorSample()
    {
        if (SampleColorAt(_lastPointerPoint) is { } color)
        {
            CopyText?.Invoke(FormatColor(color, _colorFormat));
            _noteColor = color;
            UpdateStyleButtons();
        }

        ExitColorSampling();
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
        if (max == r)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g)
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
    {        if (rect.Width < 1 || rect.Height < 1)
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
