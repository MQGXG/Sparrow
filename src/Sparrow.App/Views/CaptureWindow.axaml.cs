using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

    /// <summary>鼠标停稳多久后才去查控件树。</summary>
    private const int ControlProbeDelayMilliseconds = 120;

    /// <summary>一次控件探测超过这个时间视为卡住，允许新的探测。</summary>
    private const long ProbeStaleMilliseconds = 2000;

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
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor NorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor WestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor NwseCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor NeswCursor = new(StandardCursorType.TopRightCorner);

    private readonly Dictionary<ResizeHandle, Ellipse> _handles = [];

    private PixelPoint _origin;
    private double _scaling = 1;
    private int _virtualWidth;
    private int _virtualHeight;
    private byte[]? _frozenPixels;
    private int _frozenWidth;
    private int _frozenHeight;
    private WriteableBitmap? _frozenBitmap;
    private OcrBoxMode _ocrBoxMode = OcrBoxMode.Hover;
    private readonly List<Rectangle> _ocrBoxes = [];
    private Rectangle? _hoveredOcrBox;
    private IReadOnlyList<WindowCandidate> _candidates = [];
    private WindowCandidate? _currentCandidate;
    private WindowCandidate? _candidateAtPress;
    private UiElementCandidate? _uiCandidate;
    private UiElementCandidate? _uiCandidateAtPress;
    private bool _probeInFlight;
    private long _probeStartedAt;
    private bool _forceWindowCandidate;
    private int _probeGeneration;
    private Point _lastPointerPoint;
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

        OcrLayer.PointerMoved += OnOcrLayerPointerMoved;
        OcrLayer.PointerExited += OnOcrLayerPointerExited;
    }

    /// <summary>显示前是否先冻结整屏；测试/无桌面场景由调用方关掉。</summary>
    public bool FreezeOnOpen { get; set; }

    /// <summary>识别框显示方式；工具栏或快捷键切换后回调宿主持久化。</summary>
    public OcrBoxMode CaptureOcrBoxMode
    {
        get => _ocrBoxMode;
        set
        {
            _ocrBoxMode = value;
            ApplyOcrBoxMode();
        }
    }

    public Action<OcrBoxMode>? OcrBoxModeChanged { get; set; }

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

        _candidates = WindowCandidates.Enumerate(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);

        if (FreezeOnOpen)
            _ = FreezeScreenAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _probeGeneration++;
        _ocrCancellation?.Cancel();
        _ocrCancellation?.Dispose();
        _ocrCancellation = null;

        _frozenBitmap?.Dispose();
        _frozenBitmap = null;
        _frozenPixels = null;

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
        _virtualWidth = right - left;
        _virtualHeight = bottom - top;

        Position = _origin;
        Width = (right - left) / _scaling;
        Height = (bottom - top) / _scaling;
    }

    /// <summary>
    /// 打开时先抓一张稳定帧并冻结；之后的 OCR / 保存 / 复制都从这张图裁剪，
    /// 不再二次抓屏，也不会因为滚动惯性拿到上一帧。
    /// </summary>
    private async Task FreezeScreenAsync()
    {
        Opacity = 0;

        try
        {
            // 等遮罩真正透明（DWM 合成一帧）再抓，否则抓到的就是遮罩自己
            await Task.Delay(120);

            var width = _virtualWidth;
            var height = _virtualHeight;

            var pixels = await Task.Run(() => ScreenCapture.CaptureStableRaw(
                _origin.X,
                _origin.Y,
                width,
                height));

            if (pixels is null || pixels.Length != width * height * 4 || !IsVisible)
                return;

            _frozenPixels = pixels;
            _frozenWidth = width;
            _frozenHeight = height;

            _frozenBitmap?.Dispose();
            _frozenBitmap = CreateBitmap(pixels, width, height);

            FrozenFrame.Source = _frozenBitmap;
            FrozenFrame.Width = Width;
            FrozenFrame.Height = Height;
            Canvas.SetLeft(FrozenFrame, 0);
            Canvas.SetTop(FrozenFrame, 0);
            FrozenFrame.IsVisible = true;
        }
        catch
        {
            // 抓不到就退回实时桌面，不影响框选
        }
        finally
        {
            Opacity = 1;
        }
    }

    private static WriteableBitmap CreateBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var buffer = bitmap.Lock();
        var stride = width * 4;

        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(
                pixels,
                row * stride,
                buffer.Address + row * buffer.RowBytes,
                stride);
        }

        return bitmap;
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
        // 任何按下都先结束候选态：拖动就是自由选区，单击才用候选窗口
        _candidateAtPress = _currentCandidate;
        _uiCandidateAtPress = _uiCandidate is not null && Contains(_uiCandidate.Bounds, ToPhysical(point))
            ? _uiCandidate
            : null;
        HideCandidate();

        BeginCreate(point);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        _lastPointerPoint = point;

        // 没有选区、没有 OCR overlay 时，悬停高亮光标下的窗口
        if (_mode == DragMode.None && !_hasSelection && !OcrLayer.IsVisible)
        {
            UpdateCandidateAt(point);
        }
        else
        {
            HideCandidate();
        }

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

        var candidateAtPress = _candidateAtPress;
        var uiCandidateAtPress = _uiCandidateAtPress;
        _candidateAtPress = null;
        _uiCandidateAtPress = null;

        if (mode == DragMode.Create)
        {
            ApplyCreate(e.GetPosition(this));

            if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            {
                // 没拖动就是一次单击：有控件候选选中控件，否则选中窗口
                if (candidateAtPress is not null)
                {
                    ApplyCandidate(candidateAtPress, uiCandidateAtPress);
                    return;
                }

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

    /// <summary>鼠标悬停：高亮窗口，并在短暂停顿后细化到 UIA 控件。</summary>
    private void UpdateCandidateAt(Point point)
    {
        _forceWindowCandidate = false;

        var physical = ToPhysical(point);

        if (_uiCandidate is not null && !Contains(_uiCandidate.Bounds, physical))
            SetUiCandidate(null);

        SetWindowCandidate(WindowCandidates.Pick(_candidates, physical));

        ScheduleControlProbe();
    }

    private PixelPoint ToPhysical(Point point) => new(
        _origin.X + (int)Math.Round(point.X * _scaling),
        _origin.Y + (int)Math.Round(point.Y * _scaling));

    private void SetWindowCandidate(WindowCandidate? candidate)
    {
        if (candidate?.Handle == _currentCandidate?.Handle)
            return;

        _currentCandidate = candidate;
        _uiCandidate = null;

        if (candidate is null)
        {
            HideCandidate();
            return;
        }

        ShowCandidateVisual(candidate.Bounds, candidate.Title, kind: null);
    }

    private void SetUiCandidate(UiElementCandidate? candidate)
    {
        _uiCandidate = candidate;

        if (candidate is not null)
        {
            ShowCandidateVisual(candidate.Bounds, candidate.Title, candidate.ControlType);
            return;
        }

        if (_currentCandidate is not null)
            ShowCandidateVisual(_currentCandidate.Bounds, _currentCandidate.Title, kind: null);
        else
            HideCandidate();
    }

    private void ShowCandidateVisual(PixelRect bounds, string title, string? kind)
    {
        var x = (bounds.X - _origin.X) / _scaling;
        var y = (bounds.Y - _origin.Y) / _scaling;
        var width = bounds.Width / _scaling;
        var height = bounds.Height / _scaling;

        Canvas.SetLeft(CandidateBox, x);
        Canvas.SetTop(CandidateBox, y);
        CandidateBox.Width = width;
        CandidateBox.Height = height;
        CandidateBox.IsVisible = true;

        var name = title.Length <= 32 ? title : title[..32] + "…";

        CandidateText.Text = kind is null
            ? $"{name}  {bounds.Width} × {bounds.Height}"
            : $"{kind} · {name}  {bounds.Width} × {bounds.Height}";

        Canvas.SetLeft(CandidateBadge, Math.Max(0, x));
        Canvas.SetTop(CandidateBadge, y >= 34 ? y - 30 : y + 6);
        CandidateBadge.IsVisible = true;
    }

    private void HideCandidate()
    {
        _probeGeneration++;
        _currentCandidate = null;
        _uiCandidate = null;
        CandidateBox.IsVisible = false;
        CandidateBadge.IsVisible = false;
    }

    /// <summary>单击候选：控件优先，否则整个窗口。</summary>
    private void ApplyCandidate(WindowCandidate candidate, UiElementCandidate? uiCandidate)
    {
        ApplyRect(uiCandidate?.Bounds ?? candidate.Bounds);
    }

    private void ApplyRect(PixelRect bounds)
    {
        var x = (bounds.X - _origin.X) / _scaling;
        var y = (bounds.Y - _origin.Y) / _scaling;

        var left = Math.Clamp(x, 0, Math.Max(0, Width - 1));
        var top = Math.Clamp(y, 0, Math.Max(0, Height - 1));
        var right = Math.Clamp(x + bounds.Width / _scaling, left + 1, Width);
        var bottom = Math.Clamp(y + bounds.Height / _scaling, top + 1, Height);

        _selection = new Rect(left, top, right - left, bottom - top);

        if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            return;

        _hasSelection = true;
        HideCandidate();
        UpdateSelectionVisuals();
        ShowActionBar(_selection);
        UpdateHandles();
        UpdateSizeBadge(_selection);
        Cursor = MoveCursor;
    }

    private static bool Contains(PixelRect bounds, PixelPoint point) =>
        point.X >= bounds.X && point.X < bounds.X + bounds.Width &&
        point.Y >= bounds.Y && point.Y < bounds.Y + bounds.Height;

    /// <summary>
    /// 鼠标停稳后再查 UIA，避免每次移动都跨进程遍历控件树。
    /// 每次移动都把 generation 往前推，旧的等待醒来后自行作废。
    /// </summary>
    private void ScheduleControlProbe()
    {
        if (_forceWindowCandidate || _hasSelection || OcrLayer.IsVisible || _currentCandidate is null)
            return;

        var generation = ++_probeGeneration;
        _ = ProbeAfterDelayAsync(generation);
    }

    private async Task ProbeAfterDelayAsync(int generation)
    {
        try
        {
            await Task.Delay(ControlProbeDelayMilliseconds);

            if (generation != _probeGeneration ||
                _forceWindowCandidate ||
                _hasSelection ||
                OcrLayer.IsVisible ||
                _currentCandidate is null)
            {
                return;
            }

            // 上一次探测可能卡在无响应的应用里；太久就放弃它，重新探测
            if (_probeInFlight &&
                Environment.TickCount64 - _probeStartedAt < ProbeStaleMilliseconds)
            {
                return;
            }

            var window = _currentCandidate;
            var point = WindowCandidates.GetCursorPosition();

            _probeInFlight = true;
            _probeStartedAt = Environment.TickCount64;

            try
            {
                var candidate = await Task.Run(() => UiElementProbe.Probe(window.Handle, point));

                if (generation != _probeGeneration ||
                    _currentCandidate?.Handle != window.Handle ||
                    _hasSelection ||
                    _forceWindowCandidate)
                {
                    return;
                }

                // 探测期间鼠标又动了：这次结果已过时，等下一次防抖
                var latest = WindowCandidates.GetCursorPosition();
                if (Math.Abs(latest.X - point.X) > 2 || Math.Abs(latest.Y - point.Y) > 2)
                    return;

                SetUiCandidate(candidate);
            }
            finally
            {
                _probeInFlight = false;
            }
        }
        catch
        {
            // 探测失败就保持窗口级候选
        }
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
        // 动作栏是普通控件，不该显示截图十字
        if (IsPointInActionBar(point))
        {
            Cursor = ArrowCursor;
            return;
        }

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
        HideCandidate();
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
            var png = await CaptureSelectionAsync(action);
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

    private async Task<byte[]?> CaptureSelectionAsync(CaptureAction action)
    {
        var rect = _selection;
        var x = (int)Math.Round(rect.X * _scaling);
        var y = (int)Math.Round(rect.Y * _scaling);
        var width = (int)Math.Round(rect.Width * _scaling);
        var height = (int)Math.Round(rect.Height * _scaling);

        SelectionRect = new PixelRect(_origin.X + x, _origin.Y + y, width, height);

        // 有冻结帧：直接从冻结图裁剪，不再抓屏；滚动截图必须用实时画面
        if (action != CaptureAction.Scroll && _frozenPixels is not null)
        {
            var cropped = ScreenCapture.Crop(
                _frozenPixels,
                _frozenWidth,
                _frozenHeight,
                x,
                y,
                width,
                height);

            return cropped is null ? null : ScreenCapture.EncodePng(cropped, width, height);
        }

        // 退回实时抓屏（滚动截图 / 冻结失败）
        Opacity = 0;
        try
        {
            await Task.Delay(80);

            var absoluteX = _origin.X + x;
            var absoluteY = _origin.Y + y;

            return await Task.Run(() =>
                ScreenCapture.CaptureStable(absoluteX, absoluteY, width, height));
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
        _ocrBoxes.Clear();
        _hoveredOcrBox = null;
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
            _ocrBoxes.Add(box);
            Canvas.SetLeft(box, line.X / _scaling - OcrLinePadding);
            Canvas.SetTop(box, line.Y / _scaling - OcrLinePadding);
        }

        RecognizeButton.Content = "重新识别";
        CopyAllButton.IsVisible = true;
        OcrBoxButton.IsVisible = true;
        OcrStatus.IsVisible = true;
        ApplyOcrBoxMode();
    }

    private void ShowOcrError(string message)
    {
        OcrStatus.IsVisible = true;
        OcrStatus.Text = message;
    }

    private void ClearOcrOverlay()
    {
        OcrLayer.Children.Clear();
        _ocrBoxes.Clear();
        _hoveredOcrBox = null;
        OcrLayer.IsVisible = false;
        CopyAllButton.IsVisible = false;
        OcrBoxButton.IsVisible = false;
        OcrStatus.IsVisible = false;
        RecognizeButton.Content = "识别文字";
        _ocrText = null;
        UpdateHandles();
    }

    private void OnOcrBoxModeClick(object? sender, RoutedEventArgs e) => CycleOcrBoxMode();

    private void CycleOcrBoxMode()
    {
        _ocrBoxMode = OcrBoxModes.Next(_ocrBoxMode);
        ApplyOcrBoxMode();
        OcrBoxModeChanged?.Invoke(_ocrBoxMode);
    }

    /// <summary>按模式统一设置行框：全部显示 / 悬停 / 关闭。</summary>
    private void ApplyOcrBoxMode()
    {
        OcrBoxButton.Content = OcrBoxModes.Label(_ocrBoxMode);
        OcrBoxButton.IsVisible = OcrLayer.IsVisible;
        OcrLayer.IsHitTestVisible = _ocrBoxMode != OcrBoxMode.Off;
        _hoveredOcrBox = null;

        foreach (var box in _ocrBoxes)
        {
            box.Fill = LineFill;
            box.IsVisible = _ocrBoxMode == OcrBoxMode.All;
        }

        if (OcrLayer.IsVisible)
            UpdateOcrStatus();
    }

    private void UpdateOcrStatus()
    {
        var hint = _ocrBoxMode switch
        {
            OcrBoxMode.All => "点行复制",
            OcrBoxMode.Hover => "悬停行可点",
            _ => "行框已关闭"
        };

        OcrStatus.Text = $"{_ocrBoxes.Count} 行 · {hint}";
    }

    private void OnOcrLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_ocrBoxMode != OcrBoxMode.Hover || _ocrBoxes.Count == 0)
            return;

        var point = e.GetPosition(OcrLayer);
        Rectangle? hit = null;

        foreach (var box in _ocrBoxes)
        {
            var left = Canvas.GetLeft(box);
            var top = Canvas.GetTop(box);

            if (double.IsNaN(left) || double.IsNaN(top))
                continue;

            // 行框本身很薄，外扩一点更好点
            var rect = new Rect(left - 4, top - 4, box.Width + 8, box.Height + 8);
            if (rect.Contains(point))
            {
                hit = box;
                break;
            }
        }

        if (ReferenceEquals(hit, _hoveredOcrBox))
            return;

        if (_hoveredOcrBox is not null)
        {
            _hoveredOcrBox.IsVisible = false;
            _hoveredOcrBox.Fill = LineFill;
        }

        _hoveredOcrBox = hit;

        if (hit is not null)
        {
            hit.IsVisible = true;
            hit.Fill = LineHoverFill;
        }
    }

    private void OnOcrLayerPointerExited(object? sender, PointerEventArgs e)
    {
        if (_hoveredOcrBox is null)
            return;

        _hoveredOcrBox.IsVisible = false;
        _hoveredOcrBox.Fill = LineFill;
        _hoveredOcrBox = null;
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

        // Tab：没有选区时切换窗口；控件级由鼠标停顿后自动探测
        if (key == Key.Tab && !_hasSelection && !OcrLayer.IsVisible && _candidates.Count > 0)
        {
            _forceWindowCandidate = true;
            _probeGeneration++;
            _uiCandidate = null;

            var previous = _currentCandidate;
            _currentCandidate = null;

            var next = WindowCandidates.Next(
                _candidates,
                WindowCandidates.GetCursorPosition(),
                previous);

            if (next is null)
            {
                HideCandidate();
            }
            else
            {
                _currentCandidate = next;
                ShowCandidateVisual(next.Bounds, next.Title, kind: null);
            }

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
