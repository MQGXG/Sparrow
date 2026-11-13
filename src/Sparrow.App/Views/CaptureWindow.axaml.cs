using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Sparrow.App.Services;

namespace Sparrow.App.Views;

/// <summary>截图完成后用户选择的动作。</summary>
public enum CaptureAction
{
    None,
    Recognize,
    CopyImage,
    Save,
    Pin,

    /// <summary>进入滚动截图；结果由 ScrollResultWindow 再选择动作。</summary>
    Scroll
}

/// <summary>
/// 全屏选区窗口。
/// 用鼠标拖出矩形，松开后截取该区域（物理像素），然后显示动作栏。
/// </summary>
public partial class CaptureWindow : Window
{
    private const double OcrLinePadding = 2;

    private static readonly IBrush LineFill = new SolidColorBrush(Color.Parse("#1FE5A44C"));
    private static readonly IBrush LineHoverFill = new SolidColorBrush(Color.Parse("#40E5A44C"));
    private static readonly IBrush LineStroke = new SolidColorBrush(Color.Parse("#80E5A44C"));

    private Point? _start;
    private PixelPoint _origin;
    private double _scaling = 1;
    private Rect _selection;
    private CancellationTokenSource? _ocrCancellation;
    private bool _isRecognizing;
    private string? _ocrText;

    public CaptureWindow() => InitializeComponent();

    /// <summary>
    /// 宿主注入的 OCR 执行器。为空时「识别文字」保持旧行为：
    /// 关窗、由宿主走面板识别。
    /// </summary>
    public Func<byte[], CancellationToken, Task<CaptureOcrResult>>? OcrRunner { get; set; }

    /// <summary>宿主注入的复制实现。窗口不直接碰剪贴板。</summary>
    public Action<string>? CopyText { get; set; }

    /// <summary>插件只返回文本时的回退内容，由宿主在窗口关闭后展示。</summary>
    public string? FallbackText { get; private set; }

    /// <summary>选区的 PNG 结果；取消或选区过小时为 null。</summary>
    public byte[]? Result { get; private set; }

    /// <summary>用户选择的动作。</summary>
    public CaptureAction Action { get; private set; }

    /// <summary>选区的物理像素矩形（滚动截图要按这个区域循环抓帧）。</summary>
    public PixelRect SelectionRect { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyVirtualScreenBounds();
    }

    protected override void OnClosed(EventArgs e)
    {
        _ocrCancellation?.Cancel();
        _ocrCancellation?.Dispose();
        _ocrCancellation = null;
        base.OnClosed(e);
    }

    /// <summary>
    /// 覆盖所有显示器的并集，而不是只覆盖主屏。
    /// 多屏缩放不同时，以主屏缩放为基准（M1 已知限制）。
    /// </summary>
    private void ApplyVirtualScreenBounds()
    {
        var screens = Screens.All;
        if (screens.Count == 0)
            return;

        var left = screens.Min(screen => screen.Bounds.X);
        var top = screens.Min(screen => screen.Bounds.Y);
        var right = screens.Max(screen => screen.Bounds.X + screen.Bounds.Width);
        var bottom = screens.Max(screen => screen.Bounds.Y + screen.Bounds.Height);

        _origin = new PixelPoint(left, top);
        _scaling = Screens.Primary?.Scaling ?? RenderScaling;

        Position = _origin;
        Width = (right - left) / _scaling;
        Height = (bottom - top) / _scaling;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // 已经截完图：不允许再拉新选区，否则 overlay 会被误清掉
        if (Result is not null)
            return;

        _start = e.GetPosition(this);

        Selection.IsVisible = true;
        Canvas.SetLeft(Selection, _start.Value.X);
        Canvas.SetTop(Selection, _start.Value.Y);
        Selection.Width = 0;
        Selection.Height = 0;

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_start is null)
            return;

        UpdateSelection(e.GetPosition(this));
        e.Handled = true;
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_start is null)
            return;

        var end = e.GetPosition(this);
        UpdateSelection(end);

        var start = _start.Value;
        _start = null;

        var rect = new Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));

        if (rect.Width < 4 || rect.Height < 4)
        {
            Close();
            return;
        }

        var scaling = _scaling;
        var origin = _origin;

        var x = origin.X + (int)(rect.X * scaling);
        var y = origin.Y + (int)(rect.Y * scaling);
        var width = (int)(rect.Width * scaling);
        var height = (int)(rect.Height * scaling);

        SelectionRect = new PixelRect(x, y, width, height);
        _selection = rect;

        // 先让遮罩透明，再截图，否则截到的是自己
        Opacity = 0;
        await Task.Delay(80);

        Result = ScreenCapture.Capture(x, y, width, height);
        Opacity = 1;

        if (Result is null)
        {
            Close();
            return;
        }

        ShowActionBar(rect);
    }

    private void ShowActionBar(Rect selection)
    {
        ActionBar.IsVisible = true;

        const double estimatedWidth = 620;
        var left = Math.Max(8, Math.Min(selection.X, Width - estimatedWidth - 8));
        var top = selection.Bottom + 12;

        // 选区贴近底部时，动作栏放到选区上方
        if (top + 56 > Height)
            top = Math.Max(8, selection.Y - 56);

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, top);
    }

    private async void OnRecognizeClick(object? sender, RoutedEventArgs e) => await RunOcrAsync();

    private void OnCopyAllClick(object? sender, RoutedEventArgs e) => CopyAllText();

    private async Task RunOcrAsync()
    {
        // 没有注入 runner 或还没截图时，退回旧流程
        if (OcrRunner is null || Result is not { Length: > 0 } png)
        {
            Action = CaptureAction.Recognize;
            Close();
            return;
        }

        if (_isRecognizing)
            return;

        _isRecognizing = true;
        _ocrCancellation = new CancellationTokenSource();
        RecognizeButton.IsEnabled = false;
        OcrStatus.IsVisible = true;
        OcrStatus.Text = "正在识别…";

        try
        {
            var result = await OcrRunner(png, _ocrCancellation.Token);

            if (!string.IsNullOrEmpty(result.Error))
            {
                ShowOcrError(result.Error);
                return;
            }

            // 旧插件只返回文本：关窗，让宿主回退到面板
            if (result.IsFallback)
            {
                FallbackText = result.Text;
                Action = CaptureAction.Recognize;
                Close();
                return;
            }

            ShowOcrResult(result);
        }
        catch (OperationCanceledException)
        {
            OcrStatus.Text = "已取消";
        }
        catch (Exception ex)
        {
            ShowOcrError($"识别失败：{ex.Message}");
        }
        finally
        {
            _isRecognizing = false;
            RecognizeButton.IsEnabled = true;
            _ocrCancellation?.Dispose();
            _ocrCancellation = null;
        }
    }

    /// <summary>把行框叠在选区上；坐标按主屏缩放换算回 DIP。</summary>
    internal void ShowOcrResult(CaptureOcrResult result)
    {
        _ocrText = result.Text;
        OcrLayer.Children.Clear();

        var left = Canvas.GetLeft(Selection);
        var top = Canvas.GetTop(Selection);
        if (double.IsNaN(left)) left = _selection.X;
        if (double.IsNaN(top)) top = _selection.Y;

        OcrLayer.Width = Selection.Width;
        OcrLayer.Height = Selection.Height;
        Canvas.SetLeft(OcrLayer, left);
        Canvas.SetTop(OcrLayer, top);
        OcrLayer.IsVisible = true;

        foreach (var line in result.Lines)
        {
            var height = Math.Max(line.Height / _scaling, 8);

            var box = new Rectangle
            {
                Width = Math.Max(1, line.Width / _scaling + OcrLinePadding * 2),
                Height = height + OcrLinePadding * 2,
                Fill = LineFill,
                Stroke = LineStroke,
                StrokeThickness = 1,
                RadiusX = 2,
                RadiusY = 2,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = line.Text.Trim()
            };

            box.PointerEntered += OnOcrLineEntered;
            box.PointerExited += OnOcrLineExited;
            box.PointerPressed += OnOcrLinePressed;

            OcrLayer.Children.Add(box);
            Canvas.SetLeft(box, line.X / _scaling - OcrLinePadding);
            Canvas.SetTop(box, line.Y / _scaling - OcrLinePadding);
        }

        RecognizeButton.Content = "重新识别";
        CopyAllButton.IsVisible = true;
        OcrStatus.IsVisible = true;
        OcrStatus.Text = $"{result.Lines.Count} 行 · 点行复制";
    }

    private void ShowOcrError(string message)
    {
        OcrStatus.IsVisible = true;
        OcrStatus.Text = message;
    }

    private void ClearOcrOverlay()
    {
        OcrLayer.Children.Clear();
        OcrLayer.IsVisible = false;
        CopyAllButton.IsVisible = false;
        OcrStatus.IsVisible = false;
        RecognizeButton.Content = "识别文字";
        _ocrText = null;
    }

    private void CopyAllText()
    {
        if (string.IsNullOrEmpty(_ocrText))
            return;

        CopyText?.Invoke(_ocrText);
        OcrStatus.Text = $"已复制全文 · {_ocrText.Length} 字";
    }

    private void OnOcrLineEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Rectangle box)
            box.Fill = LineHoverFill;
    }

    private void OnOcrLineExited(object? sender, PointerEventArgs e)
    {
        if (sender is Rectangle box)
            box.Fill = LineFill;
    }

    private void OnOcrLinePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Rectangle { Tag: string text })
        {
            CopyText?.Invoke(text);
            OcrStatus.Text = $"已复制：{Shorten(text)}";
            e.Handled = true;
        }
    }

    private static string Shorten(string text) =>
        text.Length <= 18 ? text : text[..18] + "…";

    private void OnCopyImageClick(object? sender, RoutedEventArgs e)
    {
        Action = CaptureAction.CopyImage;
        Close();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        Action = CaptureAction.Save;
        Close();
    }

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        Action = CaptureAction.Pin;
        Close();
    }

    private void OnScrollClick(object? sender, RoutedEventArgs e)
    {
        Action = CaptureAction.Scroll;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Action = CaptureAction.None;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 中文输入法会把字母键标记成 ImeProcessed；
        // 截图窗口没有文本输入，按物理键还原即可，不需要放行给输入法。
        var key = e.Key == Key.ImeProcessed ? MapPhysicalKey(e.PhysicalKey) : e.Key;

        if (key == Key.Escape)
        {
            if (_isRecognizing)
            {
                _ocrCancellation?.Cancel();
                e.Handled = true;
                return;
            }

            if (OcrLayer.IsVisible)
            {
                ClearOcrOverlay();
                e.Handled = true;
                return;
            }

            Action = CaptureAction.None;
            Close();
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
            Action = action;
            Close();
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

    private void UpdateSelection(Point current)
    {
        if (_start is null)
            return;

        var start = _start.Value;
        var x = Math.Min(start.X, current.X);
        var y = Math.Min(start.Y, current.Y);

        Canvas.SetLeft(Selection, x);
        Canvas.SetTop(Selection, y);
        Selection.Width = Math.Abs(current.X - start.X);
        Selection.Height = Math.Abs(current.Y - start.Y);

        UpdateSizeBadge(x, y, Selection.Width, Selection.Height);
    }

    /// <summary>
    /// 草稿区尺寸提示：按物理像素显示，和最终截图尺寸一致。
    /// 默认放在选区右下方；贴边时翻到选区内侧，避免被屏幕边缘裁掉。
    /// </summary>
    private void UpdateSizeBadge(double x, double y, double width, double height)
    {
        if (width < 1 || height < 1)
        {
            SizeBadge.IsVisible = false;
            return;
        }

        SizeBadge.IsVisible = true;
        SizeText.Text = $"{(int)Math.Round(width * _scaling)} × {(int)Math.Round(height * _scaling)}";

        const double badgeWidth = 96;
        const double badgeHeight = 26;
        const double gap = 8;

        var left = x + width + gap;
        if (left + badgeWidth > Width)
            left = x + width - badgeWidth - gap;

        var top = y + height + gap;
        if (top + badgeHeight > Height)
            top = y + height - badgeHeight - gap;

        Canvas.SetLeft(SizeBadge, Math.Max(4, Math.Min(left, Width - badgeWidth - 4)));
        Canvas.SetTop(SizeBadge, Math.Max(4, Math.Min(top, Height - badgeHeight - 4)));
    }
}
