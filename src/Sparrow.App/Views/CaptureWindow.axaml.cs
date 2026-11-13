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
    private const double MinSelectionSize = 4;

    private enum DragMode
    {
        None,
        Create,
        Move,
        Resize
    }

    private enum ResizeHandle
    {
        None,
        NW,
        N,
        NE,
        E,
        SE,
        S,
        SW,
        W
    }

    private static readonly IBrush LineFill = new SolidColorBrush(Color.Parse("#1FE5A44C"));
    private static readonly IBrush LineHoverFill = new SolidColorBrush(Color.Parse("#40E5A44C"));
    private static readonly IBrush LineStroke = new SolidColorBrush(Color.Parse("#80E5A44C"));
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IBrush HandleStroke = new SolidColorBrush(Color.Parse("#CCE5A44C"));

    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor NorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor WestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor NwseCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor NeswCursor = new(StandardCursorType.TopRightCorner);

    private readonly Dictionary<ResizeHandle, Ellipse> _handles = [];

    private PixelPoint _origin;
    private double _scaling = 1;
    private Rect _selection;
    private Rect _selectionOrigin;
    private Point _dragOrigin;
    private Point _createStart;
    private DragMode _mode;
    private ResizeHandle _resizeHandle;
    private bool _hasSelection;
    private bool _isActing;
    private CancellationTokenSource? _ocrCancellation;
    private bool _isRecognizing;
    private string? _ocrText;

    public CaptureWindow()
    {
        InitializeComponent();
        CreateHandles();
    }

    private void CreateHandles()
    {
        foreach (var handle in new[]
                 {
                     ResizeHandle.NW, ResizeHandle.N, ResizeHandle.NE, ResizeHandle.E,
                     ResizeHandle.SE, ResizeHandle.S, ResizeHandle.SW, ResizeHandle.W
                 })
        {
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = HandleFill,
                Stroke = HandleStroke,
                StrokeThickness = 2,
                IsVisible = false
            };

            _handles[handle] = dot;
            HandleLayer.Children.Add(dot);
        }
    }

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
        if (_isActing || _isRecognizing)
            return;

        var point = e.GetPosition(this);

        // 点在动作栏上：交给按钮，不要当成新选区
        if (IsPointInActionBar(point))
            return;

        // 第一次点击先清掉识别结果，不直接开始拉新框
        if (OcrLayer.IsVisible)
        {
            if (e.Source is not Rectangle)
                ClearOcrOverlay();

            e.Handled = true;
            return;
        }

        if (_hasSelection)
        {
            var handle = HitHandle(point);
            if (handle != ResizeHandle.None)
            {
                _mode = DragMode.Resize;
                _resizeHandle = handle;
                _dragOrigin = point;
                _selectionOrigin = _selection;
                e.Handled = true;
                return;
            }

            if (_selection.Contains(point))
            {
                _mode = DragMode.Move;
                _dragOrigin = point;
                _selectionOrigin = _selection;
                Cursor = MoveCursor;
                e.Handled = true;
                return;
            }
        }

        // 框外按下：重新拉一个
        BeginCreate(point);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);

        switch (_mode)
        {
            case DragMode.Create:
                ApplyCreate(point);
                e.Handled = true;
                return;
            case DragMode.Move:
                ApplyMove(point);
                e.Handled = true;
                return;
            case DragMode.Resize:
                ApplyResize(point);
                e.Handled = true;
                return;
        }

        UpdateCursor(point);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_mode == DragMode.None)
            return;

        var mode = _mode;
        _mode = DragMode.None;

        if (mode == DragMode.Create)
        {
            ApplyCreate(e.GetPosition(this));

            if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            {
                ResetSelection();
                return;
            }

            _hasSelection = true;
            ShowActionBar(_selection);
            UpdateHandles();
            UpdateSizeBadge(_selection);
            Cursor = MoveCursor;
        }
        else
        {
            // 移动/拉伸结束：动作栏跟着新位置走
            PositionActionBar(_selection);
            UpdateHandles();
            UpdateSizeBadge(_selection);
        }

        e.Handled = true;
    }

    private void BeginCreate(Point point)
    {
        _hasSelection = false;
        _mode = DragMode.Create;
        _createStart = point;
        _selection = new Rect(point, new Size());
        _selectionOrigin = _selection;

        Result = null;
        FallbackText = null;
        ActionBar.IsVisible = false;
        HandleLayer.IsVisible = false;
        OcrStatus.IsVisible = false;
        CopyAllButton.IsVisible = false;
        RecognizeButton.Content = "识别文字";
        _ocrText = null;

        OcrLayer.Children.Clear();
        OcrLayer.IsVisible = false;

        Selection.IsVisible = true;
        ApplyCreate(point);
        Cursor = CrossCursor;
    }

    private void ApplyCreate(Point current)
    {
        var x = Math.Min(_createStart.X, current.X);
        var y = Math.Min(_createStart.Y, current.Y);
        var width = Math.Abs(current.X - _createStart.X);
        var height = Math.Abs(current.Y - _createStart.Y);

        _selection = new Rect(x, y, width, height);
        UpdateSelectionVisuals();
    }

    private void ApplyMove(Point current)
    {
        var dx = current.X - _dragOrigin.X;
        var dy = current.Y - _dragOrigin.Y;

        var left = Math.Clamp(
            _selectionOrigin.X + dx,
            0,
            Math.Max(0, Width - _selectionOrigin.Width));
        var top = Math.Clamp(
            _selectionOrigin.Y + dy,
            0,
            Math.Max(0, Height - _selectionOrigin.Height));

        _selection = new Rect(left, top, _selectionOrigin.Width, _selectionOrigin.Height);
        UpdateSelectionVisuals();
    }

    private void ApplyResize(Point current)
    {
        var dx = current.X - _dragOrigin.X;
        var dy = current.Y - _dragOrigin.Y;

        var left = _selectionOrigin.Left;
        var top = _selectionOrigin.Top;
        var right = _selectionOrigin.Right;
        var bottom = _selectionOrigin.Bottom;

        // 拖哪条边就动哪条边，对边保持不动
        if (_resizeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW)
            left = Math.Min(_selectionOrigin.Left + dx, right - MinSelectionSize);
        if (_resizeHandle is ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE)
            right = Math.Max(_selectionOrigin.Right + dx, left + MinSelectionSize);
        if (_resizeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE)
            top = Math.Min(_selectionOrigin.Top + dy, bottom - MinSelectionSize);
        if (_resizeHandle is ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE)
            bottom = Math.Max(_selectionOrigin.Bottom + dy, top + MinSelectionSize);

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(Width, right);
        bottom = Math.Min(Height, bottom);

        _selection = new Rect(left, top, right - left, bottom - top);
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        Selection.IsVisible = _selection.Width > 0 && _selection.Height > 0;
        Canvas.SetLeft(Selection, _selection.X);
        Canvas.SetTop(Selection, _selection.Y);
        Selection.Width = _selection.Width;
        Selection.Height = _selection.Height;

        UpdateSizeBadge(_selection);

        if (_hasSelection)
        {
            UpdateHandles();
            PositionActionBar(_selection);
        }
    }

    private void UpdateHandles()
    {
        HandleLayer.IsVisible = _hasSelection && !OcrLayer.IsVisible;

        if (!HandleLayer.IsVisible)
            return;

        foreach (var (handle, dot) in _handles)
        {
            dot.IsVisible = true;
            Canvas.SetLeft(dot, HandleX(handle) - dot.Width / 2);
            Canvas.SetTop(dot, HandleY(handle) - dot.Height / 2);
        }
    }

    private double HandleX(ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW => _selection.Left,
        ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE => _selection.Right,
        _ => _selection.Center.X
    };

    private double HandleY(ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE => _selection.Top,
        ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE => _selection.Bottom,
        _ => _selection.Center.Y
    };

    private ResizeHandle HitHandle(Point point)
    {
        const double radius = 7;
        var rect = _selection;

        var nearLeft = Math.Abs(point.X - rect.Left) <= radius;
        var nearRight = Math.Abs(point.X - rect.Right) <= radius;
        var nearTop = Math.Abs(point.Y - rect.Top) <= radius;
        var nearBottom = Math.Abs(point.Y - rect.Bottom) <= radius;
        var withinX = point.X >= rect.Left - radius && point.X <= rect.Right + radius;
        var withinY = point.Y >= rect.Top - radius && point.Y <= rect.Bottom + radius;

        if (nearLeft && nearTop)
            return ResizeHandle.NW;
        if (nearRight && nearTop)
            return ResizeHandle.NE;
        if (nearLeft && nearBottom)
            return ResizeHandle.SW;
        if (nearRight && nearBottom)
            return ResizeHandle.SE;
        if (nearLeft && withinY)
            return ResizeHandle.W;
        if (nearRight && withinY)
            return ResizeHandle.E;
        if (nearTop && withinX)
            return ResizeHandle.N;
        if (nearBottom && withinX)
            return ResizeHandle.S;

        return ResizeHandle.None;
    }

    private void UpdateCursor(Point point)
    {
        if (!_hasSelection || OcrLayer.IsVisible)
        {
            Cursor = CrossCursor;
            return;
        }

        Cursor = HitHandle(point) switch
        {
            ResizeHandle.NW or ResizeHandle.SE => NwseCursor,
            ResizeHandle.NE or ResizeHandle.SW => NeswCursor,
            ResizeHandle.N or ResizeHandle.S => NorthSouthCursor,
            ResizeHandle.E or ResizeHandle.W => WestEastCursor,
            _ => _selection.Contains(point) ? MoveCursor : CrossCursor
        };
    }

    private void ResetSelection()
    {
        _hasSelection = false;
        _selection = default;
        Selection.IsVisible = false;
        HandleLayer.IsVisible = false;
        ActionBar.IsVisible = false;
        SizeBadge.IsVisible = false;
        Cursor = CrossCursor;
    }

    private void ShowActionBar(Rect selection)
    {
        ActionBar.IsVisible = true;
        PositionActionBar(selection);
    }

    private void PositionActionBar(Rect selection)
    {
        if (!ActionBar.IsVisible)
            return;

        const double estimatedWidth = 620;
        var left = Math.Max(8, Math.Min(selection.X, Width - estimatedWidth - 8));
        var top = selection.Bottom + 12;

        // 选区贴近底部时，动作栏放到选区上方
        if (top + 56 > Height)
            top = Math.Max(8, selection.Y - 56);

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, top);
    }

    private bool IsPointInActionBar(Point point)
    {
        if (!ActionBar.IsVisible)
            return false;

        if (ActionBar.TranslatePoint(new Point(0, 0), this) is not { } origin)
            return false;

        return new Rect(origin, ActionBar.Bounds.Size).Contains(point);
    }

    private async void OnRecognizeClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.Recognize);

    private void OnCopyAllClick(object? sender, RoutedEventArgs e) => CopyAllText();

    /// <summary>
    /// 动作触发时才截图：选区在松手后还可以移动/拉伸，
    /// 直到用户点识别、复制、保存、固定或滚动。
    /// </summary>
    private async Task ActAsync(CaptureAction action)
    {
        if (_isActing || _isRecognizing || !_hasSelection)
            return;

        if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            return;

        _isActing = true;

        try
        {
            var png = await CaptureSelectionAsync();
            if (png is null)
            {
                Action = CaptureAction.None;
                Close();
                return;
            }

            // 抓图期间用户可能已经按 Esc 关窗
            if (!IsVisible)
                return;

            Result = png;
            Action = action;

            if (action == CaptureAction.Recognize && OcrRunner is not null)
            {
                await RunOcrAsync();
                return;
            }

            Close();
        }
        finally
        {
            _isActing = false;
        }
    }

    private async Task<byte[]?> CaptureSelectionAsync()
    {
        var rect = _selection;
        var x = _origin.X + (int)Math.Round(rect.X * _scaling);
        var y = _origin.Y + (int)Math.Round(rect.Y * _scaling);
        var width = (int)Math.Round(rect.Width * _scaling);
        var height = (int)Math.Round(rect.Height * _scaling);

        SelectionRect = new PixelRect(x, y, width, height);

        // 先让遮罩透明，再截图，否则截到的是自己
        Opacity = 0;
        try
        {
            await Task.Delay(80);
            return ScreenCapture.Capture(x, y, width, height);
        }
        finally
        {
            Opacity = 1;
        }
    }

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
        HandleLayer.IsVisible = false;

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
        UpdateHandles();
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

    private async void OnCopyImageClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.CopyImage);

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.Save);

    private async void OnPinClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.Pin);

    private async void OnScrollClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.Scroll);

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

    /// <summary>
    /// 选区读数：左上角物理坐标 + 物理像素尺寸，和最终截图一致。
    /// 默认放在选区上方；顶到屏幕边缘时放到下方。
    /// </summary>
    private void UpdateSizeBadge(Rect rect)
    {
        if (rect.Width < 1 || rect.Height < 1)
        {
            SizeBadge.IsVisible = false;
            return;
        }

        SizeBadge.IsVisible = true;

        var x = _origin.X + (int)Math.Round(rect.X * _scaling);
        var y = _origin.Y + (int)Math.Round(rect.Y * _scaling);
        var width = (int)Math.Round(rect.Width * _scaling);
        var height = (int)Math.Round(rect.Height * _scaling);

        SizeText.Text = $"{x}, {y}  {width} × {height}";

        const double badgeWidth = 180;
        const double badgeHeight = 26;
        const double gap = 8;

        var left = Math.Max(4, Math.Min(rect.X, Width - badgeWidth - 4));
        var top = rect.Y - badgeHeight - gap;
        if (top < 4)
            top = rect.Y + 4; // 顶到屏幕边缘时放进选区内侧，避免压住动作栏
        top = Math.Max(4, Math.Min(top, Height - badgeHeight - 4));

        Canvas.SetLeft(SizeBadge, left);
        Canvas.SetTop(SizeBadge, top);
    }
}
