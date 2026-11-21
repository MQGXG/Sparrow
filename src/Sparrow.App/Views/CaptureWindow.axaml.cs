using System.Runtime;
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
using Sparrow.App.Settings;

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

    // 行框只画边框：填充透明（Transparent 而不是 null，否则中间不参与命中，点行复制会失效）
    private static readonly IBrush LineFill = Brushes.Transparent;
    private static readonly IBrush LineStroke = new SolidColorBrush(Color.Parse("#80E5A44C"));
    private static readonly IBrush LineHoverStroke = new SolidColorBrush(Color.Parse("#FFE5A44C"));
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
    private UiElementProbe? _uiProbe;
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

    /// <summary>当前会话的标注；导出时合成进 PNG。</summary>
    private readonly AnnotationDocument _notes = new();

    private AnnotationTool _tool = AnnotationTool.None;
    private Point _annotationStart;
    private IAnnotation? _pendingAnnotation;
    private List<Point>? _penPoints;

    private static readonly Color NoteColor = Color.Parse("#E5A44C");
    private const double NoteWidth = 3;
    private const double HighlighterWidth = 18;
    private const double HighlighterOpacity = 0.4;
    private const int MosaicCell = 12;
    private const double TextFontSize = 16;
    private const double NumberSize = 24;

    private int _nextNumber = 1;
    private Point _textOrigin;

    // 工具条拖动：手动位置之后不再跟随选区
    private bool _draggingToolbar;
    private bool _toolbarManual;
    private Point _toolbarDragStart;
    private Point _toolbarStartPosition;

    /// <summary>选区覆盖的那个窗口（DIP）；拖动/拉伸时吸附它的边和中线。</summary>
    private Rect? _snapBox;

    /// <summary>拖动开始时算一次选区所在显示器（DIP）。</summary>
    private Rect? _activeScreen;

    /// <summary>尺寸角标显示物理像素还是 DIP。</summary>
    private bool _sizeInPixels = true;

    /// <summary>导出圆角半径（0 = 不切）。</summary>
    private double _cornerRadius;

    public double ExportCornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = Math.Max(0, value);
    }

    /// <summary>标注样式（颜色/粗细/马赛克模式），由宿主读写持久化。</summary>
    public uint NoteColorValue
    {
        get => _noteColor.ToUInt32();
        set => _noteColor = Color.FromUInt32(value);
    }

    public double NoteStrokeWidth
    {
        get => _noteWidth;
        set => _noteWidth = value;
    }

    public bool NoteMosaicBlur
    {
        get => _mosaicBlur;
        set => _mosaicBlur = value;
    }

    public bool SizeInPixels
    {
        get => _sizeInPixels;
        set => _sizeInPixels = value;
    }

    // 当前画笔样式：只影响之后画的图元
    private Color _noteColor = NoteColor;
    private double _noteWidth = NoteWidth;
    private bool _mosaicBlur;

    public CaptureWindow()
    {
        InitializeComponent();
        CreateHandles();

        OcrLayer.PointerMoved += OnOcrLayerPointerMoved;
        OcrLayer.PointerExited += OnOcrLayerPointerExited;
        UpdateStyleButtons();
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
    /// <c>options.TranslateTarget</c> 非空时宿主会把「翻译为」参数带给 OCR 工具。
    /// </summary>
    public Func<byte[], CaptureOcrOptions, CancellationToken, Task<CaptureOcrResult>>? OcrRunner { get; set; }

    /// <summary>
    /// 「翻译」按钮的语言列表（值 + 显示名），来自 OCR 工具的声明。
    /// 为空时整个按钮隐藏，避免给出一个点了没反应的入口。
    /// </summary>
    public void ConfigureTranslateTargets(IReadOnlyList<(string Value, string Label)> targets)
    {
        if (TranslateButton.Flyout is not MenuFlyout menu)
        {
            TranslateButton.IsVisible = false;
            return;
        }

        menu.Items.Clear();

        foreach (var (value, label) in targets)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await RunTranslateAsync(value);
            menu.Items.Add(item);
        }

        TranslateButton.IsVisible = menu.Items.Count > 0;
    }

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
        _uiProbe = new UiElementProbe();

        // 打开遮罩时鼠标可能已经停在目标控件上，不用晃一下才出候选
        ShowCandidateUnderCursor();

        if (FreezeOnOpen)
            _ = FreezeScreenAsync();

        // 宿主在构造后注入上次的样式；这里把按钮状态同步过来
        RefreshNoteStyle();
        BuildPresetMenu();
    }

    private void RefreshNoteStyle()
    {
        UpdateStyleButtons();

        if (_hasSelection)
            UpdateSizeBadge(_selection);
    }

    /// <summary>按当前光标位置立刻显示候选（含控件级细化）。</summary>
    private void ShowCandidateUnderCursor()
    {
        if (Width <= 0 || Height <= 0)
            return;

        var cursor = WindowCandidates.GetCursorPosition();
        var local = new Point(
            (cursor.X - _origin.X) / _scaling,
            (cursor.Y - _origin.Y) / _scaling);

        if (local.X < 0 || local.Y < 0 || local.X > Width || local.Y > Height)
            return;

        UpdateCandidateAt(local);
    }

    protected override void OnClosed(EventArgs e)
    {
        _probeGeneration++;
        _uiProbe?.Dispose();
        _uiProbe = null;
        _ocrCancellation?.Cancel();
        _ocrCancellation?.Dispose();
        _ocrCancellation = null;

        _frozenBitmap?.Dispose();
        _frozenBitmap = null;
        _frozenPixels = null;

        TrimFreezeMemory();

        base.OnClosed(e);
    }

    /// <summary>
    /// 冻结帧是整屏大对象（4K 约 33MB）。抓图会把 LOH 高水位顶上去，
    /// 关窗后在后台压缩一次，把这段内存还给系统，不卡 UI。
    /// </summary>
    private static void TrimFreezeMemory()
    {
        // 没抓过整屏（测试、小屏）时没必要付这次 GC
        if (Environment.WorkingSet < 600L * 1024 * 1024)
            return;

        ThreadPool.QueueUserWorkItem(static _ =>
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            catch
            {
                // 回收失败不影响功能
            }
        });
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

        // 点在尺寸角标上：切换单位，不要当成新选区
        if (SizeBadge.IsVisible
            && SizeBadge.TranslatePoint(new Point(0, 0), this) is { } badgeOrigin
            && new Rect(badgeOrigin, SizeBadge.Bounds.Size).Contains(point))
        {
            return;
        }

        // 选了标注工具：在选区内按下就是开始画，不移动选区
        if (BeginAnnotation(point))
        {
            e.Handled = true;
            return;
        }

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
                _activeScreen = ActiveScreenDip();
                e.Handled = true;
                return;
            }

            if (_selection.Contains(point))
            {
                _mode = DragMode.Move;
                _dragOrigin = point;
                _selectionOrigin = _selection;
                _activeScreen = ActiveScreenDip();
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

        // 拖动工具条：优先于选区/标注
        if (_draggingToolbar)
        {
            MoveToolbar(point);
            e.Handled = true;
            return;
        }

        // 正在画标注：更新预览，不参与移动/拉伸选区
        if (_pendingAnnotation is not null)
        {
            UpdatePendingAnnotation(point);
            RebuildNotePreview();
            e.Handled = true;
            return;
        }

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
        if (_draggingToolbar)
        {
            _draggingToolbar = false;
            e.Handled = true;
            return;
        }

        // 画完一条标注：小于阈值当误触丢掉
        if (_pendingAnnotation is not null)
        {
            CommitPendingAnnotation();
            ClearGuides();
            e.Handled = true;
            return;
        }

        ClearGuides();

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
        ClearGuides();

        // 记住新选区落在哪个窗口上：拖动/拉伸时吸它的边和中线
        _snapBox = _candidateAtPress is { } candidate ? ToDip(candidate.Bounds) : null;

        Result = null;
        FallbackText = null;
        ActionBar.IsVisible = false;
        HandleLayer.IsVisible = false;
        OcrStatus.IsVisible = false;
        CopyAllButton.IsVisible = false;
        RecognizeButton.Content = "识别文字";
        HideResultCard();
        _notes.Clear();
        _pendingAnnotation = null;
        _penPoints = null;
        RebuildNotePreview();
        UpdateToolButtons();
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

        // 还在同一个控件里：候选不变，不用再查任何东西
        if (_uiCandidate is not null &&
            _currentCandidate is not null &&
            Contains(_uiCandidate.Bounds, physical))
        {
            return;
        }

        if (_uiCandidate is not null)
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
    /// 立刻把探测投递到 UIA 线程，不做时间防抖。
    /// 每次移动都把 generation 往前推，还没轮到的旧任务在 worker 里直接丢掉，
    /// 所以鼠标扫过时不会真的去查控件树，停下来那一刻的结果才是最接近实时的。
    /// </summary>
    private void ScheduleControlProbe()
    {
        if (_forceWindowCandidate || _hasSelection || OcrLayer.IsVisible || _currentCandidate is null)
            return;

        var generation = ++_probeGeneration;
        _ = ProbeAsync(generation);
    }

    private async Task ProbeAsync(int generation)
    {
        var window = _currentCandidate;
        var probe = _uiProbe;

        if (window is null || probe is null)
            return;

        var point = WindowCandidates.GetCursorPosition();

        try
        {
            // worker 在真正遍历前会再看一次 generation，过时的任务直接返回
            var candidate = await probe.ProbeAsync(
                window.Handle,
                point,
                () => generation == Volatile.Read(ref _probeGeneration));

            if (generation != _probeGeneration ||
                _currentCandidate?.Handle != window.Handle ||
                _hasSelection ||
                _forceWindowCandidate)
            {
                return;
            }

            // 探测期间鼠标又动了：这次结果已过时，等下一次移动重新探测
            var latest = WindowCandidates.GetCursorPosition();
            if (Math.Abs(latest.X - point.X) > 2 || Math.Abs(latest.Y - point.Y) > 2)
                return;

            SetUiCandidate(candidate);
        }
        catch
        {
            // 探测失败就保持窗口级候选
        }
    }

    private void ApplyMove(Point current)
    {
        // 框移动后截图内容变了，旧标注会错位，直接清掉
        ClearAnnotations();

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

        // 吸附：屏幕边/中线，以及选区所在窗口的边/中线
        var (snapX, guideX) = SnapGuides.SnapEdges(
            left,
            left + _selectionOrigin.Width,
            SnapGuides.Combine(Width, _snapBox, vertical: true, _activeScreen));
        var (snapY, guideY) = SnapGuides.SnapEdges(
            top,
            top + _selectionOrigin.Height,
            SnapGuides.Combine(Height, _snapBox, vertical: false, _activeScreen));

        left = Math.Clamp(left + snapX, 0, Math.Max(0, Width - _selectionOrigin.Width));
        top = Math.Clamp(top + snapY, 0, Math.Max(0, Height - _selectionOrigin.Height));

        ShowGuides(guideX, guideY);

        _selection = new Rect(left, top, _selectionOrigin.Width, _selectionOrigin.Height);
        UpdateSelectionVisuals();
    }

    private void ApplyResize(Point current)
    {
        // 框尺寸变化后截图内容变了，旧标注会错位，直接清掉
        ClearAnnotations();

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

        // 吸附：只吸正在拖的那条边
        double? guideX = null;
        double? guideY = null;

        if (_resizeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW)
        {
            var (value, guide) = SnapGuides.Snap(left, SnapGuides.Combine(Width, _snapBox, vertical: true, _activeScreen));
            left = value;
            guideX = guide;
        }
        else if (_resizeHandle is ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE)
        {
            var (value, guide) = SnapGuides.Snap(right, SnapGuides.Combine(Width, _snapBox, vertical: true, _activeScreen));
            right = value;
            guideX = guide;
        }

        if (_resizeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE)
        {
            var (value, guide) = SnapGuides.Snap(top, SnapGuides.Combine(Height, _snapBox, vertical: false, _activeScreen));
            top = value;
            guideY = guide;
        }
        else if (_resizeHandle is ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE)
        {
            var (value, guide) = SnapGuides.Snap(bottom, SnapGuides.Combine(Height, _snapBox, vertical: false, _activeScreen));
            bottom = value;
            guideY = guide;
        }

        ShowGuides(guideX, guideY);

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
        HideResultCard();
        _notes.Clear();
        _pendingAnnotation = null;
        _penPoints = null;
        RebuildNotePreview();
        UpdateToolButtons();
        Cursor = CrossCursor;
    }

    // ---------- 标注 ----------

    private void OnRectToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Rectangle);

    private void OnEllipseToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Ellipse);

    private void OnArrowToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Arrow);

    private void OnPenToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Pen);

    private void OnHighlighterToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Highlighter);

    private void OnMosaicToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Mosaic);

    private void OnTextToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Text);

    private void OnNumberToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Number);

    private void OnColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            _noteColor = Color.Parse(hex);
            UpdateStyleButtons();
        }
    }

    private void OnWidthClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && double.TryParse(text, out var width))
        {
            _noteWidth = width;
            UpdateStyleButtons();
        }
    }

    private void OnMosaicModeClick(object? sender, RoutedEventArgs e)
    {
        _mosaicBlur = !_mosaicBlur;
        UpdateStyleButtons();
    }

    private void OnCornerRadiusClick(object? sender, RoutedEventArgs e)
    {
        _cornerRadius = _cornerRadius > 0 ? 0 : 10;
        UpdateStyleButtons();
    }

    private void UpdateStyleButtons()
    {
        SetSelected(ColorAmberButton, IsColor("#E5A44C"));
        SetSelected(ColorWhiteButton, IsColor("#FFFFFF"));
        SetSelected(ColorRedButton, IsColor("#FF5F5F"));
        SetSelected(ColorGreenButton, IsColor("#5FD08C"));
        SetSelected(ColorBlueButton, IsColor("#5FA8FF"));

        SetSelected(Width2Button, Math.Abs(_noteWidth - 2) < 0.01);
        SetSelected(Width4Button, Math.Abs(_noteWidth - 4) < 0.01);
        SetSelected(Width8Button, Math.Abs(_noteWidth - 8) < 0.01);

        MosaicModeButton.Content = _mosaicBlur ? "马赛克·模糊" : "马赛克·像素";
        SetSelected(MosaicModeButton, _mosaicBlur);
        SetSelected(CornerRadiusButton, _cornerRadius > 0);
    }

    private bool IsColor(string hex) => _noteColor.ToUInt32() == Color.Parse(hex).ToUInt32();

    private void OnUndoClick(object? sender, RoutedEventArgs e) => UndoAnnotation();

    private void UndoAnnotation()
    {
        _notes.Undo();
        RebuildNotePreview();
        UpdateToolButtons();
    }

    /// <summary>数字键 → 工具；和按钮 ToolTip 里的数字保持一致。</summary>
    private static bool TryMapToolKey(Key key, out AnnotationTool tool)
    {
        tool = key switch
        {
            Key.D2 or Key.NumPad2 => AnnotationTool.Rectangle,
            Key.D3 or Key.NumPad3 => AnnotationTool.Ellipse,
            Key.D4 or Key.NumPad4 => AnnotationTool.Arrow,
            Key.D5 or Key.NumPad5 => AnnotationTool.Pen,
            Key.D6 or Key.NumPad6 => AnnotationTool.Highlighter,
            Key.D7 or Key.NumPad7 => AnnotationTool.Mosaic,
            Key.D8 or Key.NumPad8 => AnnotationTool.Text,
            Key.D9 or Key.NumPad9 => AnnotationTool.Number,
            _ => AnnotationTool.None
        };

        return tool != AnnotationTool.None;
    }

    /// <summary>选区变化或重设时清空标注：坐标相对选区，选区一动旧标注就错位。</summary>
    private void ClearAnnotations()
    {
        HideTextEditor();

        if (_notes.Items.Count == 0 && _pendingAnnotation is null)
            return;

        _notes.Clear();
        _pendingAnnotation = null;
        _penPoints = null;
        _nextNumber = 1;
        RebuildNotePreview();
        UpdateToolButtons();
    }

    private void SetTool(AnnotationTool tool)
    {
        _tool = _tool == tool ? AnnotationTool.None : tool;
        UpdateToolButtons();
    }

    private void UpdateToolButtons()
    {
        SetSelected(RectToolButton, _tool == AnnotationTool.Rectangle);
        SetSelected(EllipseToolButton, _tool == AnnotationTool.Ellipse);
        SetSelected(ArrowToolButton, _tool == AnnotationTool.Arrow);
        SetSelected(PenToolButton, _tool == AnnotationTool.Pen);
        SetSelected(HighlighterToolButton, _tool == AnnotationTool.Highlighter);
        SetSelected(MosaicToolButton, _tool == AnnotationTool.Mosaic);
        SetSelected(TextToolButton, _tool == AnnotationTool.Text);
        SetSelected(NumberToolButton, _tool == AnnotationTool.Number);
        UndoButton.IsEnabled = _notes.CanUndo;

        // 属性行只在选中标注工具时出现：没选工具时整条让出去，别挡截图
        StyleRow.IsVisible = _tool != AnnotationTool.None;
    }

    private static void SetSelected(Button button, bool selected)
    {
        if (selected)
        {
            if (!button.Classes.Contains("selected"))
                button.Classes.Add("selected");
        }
        else
        {
            button.Classes.Remove("selected");
        }
    }

    /// <summary>工具激活时在选区内按下 = 开始画；返回 true 表示这次按下已被标注消费。</summary>
    private bool BeginAnnotation(Point point)
    {
        if (_tool == AnnotationTool.None || !_hasSelection || !_selection.Contains(point))
            return false;

        _annotationStart = new Point(point.X - _selection.X, point.Y - _selection.Y);
        _penPoints = null;

        switch (_tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                _pendingAnnotation = new ShapeAnnotation(
                    new Rect(_annotationStart, new Size()),
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _tool == AnnotationTool.Ellipse);
                break;

            case AnnotationTool.Arrow:
                _pendingAnnotation = new ArrowAnnotation(
                    _annotationStart, _annotationStart, _noteColor.ToUInt32(), _noteWidth);
                break;

            case AnnotationTool.Pen:
                _penPoints = [_annotationStart];
                _pendingAnnotation = new PenAnnotation(
                    _penPoints, _noteColor.ToUInt32(), _noteWidth);
                break;

            case AnnotationTool.Highlighter:
                _penPoints = [_annotationStart];
                _pendingAnnotation = new PenAnnotation(
                    _penPoints,
                    _noteColor.ToUInt32(),
                    HighlighterWidth,
                    HighlighterOpacity);
                break;

            case AnnotationTool.Mosaic:
                _pendingAnnotation = new MosaicAnnotation(
                    new Rect(_annotationStart, new Size()), MosaicCell, _mosaicBlur);
                break;

            case AnnotationTool.Text:
                ShowTextEditor(_annotationStart);
                return true;

            case AnnotationTool.Number:
                _notes.Add(new NumberAnnotation(
                    _annotationStart, _nextNumber++, _noteColor.ToUInt32(), NumberSize));
                RebuildNotePreview();
                UpdateToolButtons();
                return true;

            default:
                return false;
        }

        RebuildNotePreview();
        return true;
    }

    private void UpdatePendingAnnotation(Point point)
    {
        // 标注坐标相对选区，超出选区就贴在边上
        var current = new Point(
            Math.Clamp(point.X - _selection.X, 0, _selection.Width),
            Math.Clamp(point.Y - _selection.Y, 0, _selection.Height));

        // 落点吸附到选区边框/中线，方便画满整幅或居中
        var (snapped, guideX, guideY) = SnapGuides.SnapToBox(
            new Point(_selection.X + current.X, _selection.Y + current.Y),
            _selection);
        current = new Point(snapped.X - _selection.X, snapped.Y - _selection.Y);
        ShowGuides(guideX, guideY);

        _pendingAnnotation = _pendingAnnotation switch
        {
            ShapeAnnotation shape => shape with { Rect = NormalizeRect(_annotationStart, current) },
            ArrowAnnotation arrow => arrow with { To = current },
            MosaicAnnotation mosaic => mosaic with { Rect = NormalizeRect(_annotationStart, current) },
            PenAnnotation pen when _penPoints is not null => AddPenPoint(pen, current),
            _ => _pendingAnnotation
        };
    }

    private PenAnnotation AddPenPoint(PenAnnotation pen, Point current)
    {
        _penPoints!.Add(current);
        return pen with { Points = _penPoints };
    }

    private static Rect NormalizeRect(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    private void CommitPendingAnnotation()
    {
        var annotation = _pendingAnnotation;
        _pendingAnnotation = null;
        _penPoints = null;

        if (annotation is null)
            return;

        // 点一下没拖动：当误触丢掉，不进撤销栈
        var keep = annotation switch
        {
            ShapeAnnotation shape => shape.Rect.Width >= 2 && shape.Rect.Height >= 2,
            ArrowAnnotation arrow => Distance(arrow.From, arrow.To) >= 6,
            PenAnnotation pen => pen.Points.Count >= 2,
            MosaicAnnotation mosaic => mosaic.Rect.Width >= 4 && mosaic.Rect.Height >= 4,
            _ => false
        };

        if (keep)
            _notes.Add(annotation);

        RebuildNotePreview();
        UpdateToolButtons();
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>把已提交的标注 + 正在画的那条重画到预览层。</summary>
    private void RebuildNotePreview()
    {
        NoteLayer.Children.Clear();

        if (!_hasSelection || (_notes.Items.Count == 0 && _pendingAnnotation is null))
        {
            NoteLayer.IsVisible = false;
            return;
        }

        NoteLayer.IsVisible = true;
        NoteLayer.Width = _selection.Width;
        NoteLayer.Height = _selection.Height;
        Canvas.SetLeft(NoteLayer, _selection.X);
        Canvas.SetTop(NoteLayer, _selection.Y);

        foreach (var item in _notes.Items)
            AddPreviewShape(item);

        if (_pendingAnnotation is not null)
            AddPreviewShape(_pendingAnnotation);
    }

    private void AddPreviewShape(IAnnotation item)
    {
        switch (item)
        {
            case ShapeAnnotation { Ellipse: true } shape:
                var ellipse = new Ellipse
                {
                    Width = shape.Rect.Width,
                    Height = shape.Rect.Height,
                    Stroke = new SolidColorBrush(Color.FromUInt32(shape.Color)),
                    StrokeThickness = shape.Width
                };
                Canvas.SetLeft(ellipse, shape.Rect.X);
                Canvas.SetTop(ellipse, shape.Rect.Y);
                NoteLayer.Children.Add(ellipse);
                break;

            case ShapeAnnotation shape:
                var rectangle = new Rectangle
                {
                    Width = shape.Rect.Width,
                    Height = shape.Rect.Height,
                    Stroke = new SolidColorBrush(Color.FromUInt32(shape.Color)),
                    StrokeThickness = shape.Width
                };
                Canvas.SetLeft(rectangle, shape.Rect.X);
                Canvas.SetTop(rectangle, shape.Rect.Y);
                NoteLayer.Children.Add(rectangle);
                break;

            case ArrowAnnotation arrow:
                var arrowBrush = new SolidColorBrush(Color.FromUInt32(arrow.Color));
                AddPreviewLine(arrowBrush, arrow.From, arrow.To, arrow.Width);
                var (left, right) = ArrowGeometry.Head(
                    arrow.From, arrow.To, Math.Max(8, arrow.Width * 3.5));
                AddPreviewLine(arrowBrush, arrow.To, left, arrow.Width);
                AddPreviewLine(arrowBrush, arrow.To, right, arrow.Width);
                break;

            case PenAnnotation { Points.Count: >= 2 } pen:
                NoteLayer.Children.Add(new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromUInt32(pen.Color), pen.Opacity),
                    StrokeThickness = pen.Width,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Points = new List<Point>(pen.Points)
                });
                break;

            case MosaicAnnotation mosaic:
                var cover = new Rectangle
                {
                    Width = mosaic.Rect.Width,
                    Height = mosaic.Rect.Height,
                    Fill = new SolidColorBrush(Color.Parse("#B0808080")),
                    Stroke = new SolidColorBrush(NoteColor, 0.6),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(cover, mosaic.Rect.X);
                Canvas.SetTop(cover, mosaic.Rect.Y);
                NoteLayer.Children.Add(cover);
                break;

            case TextAnnotation text:
                var label = new TextBlock
                {
                    Text = text.Text,
                    FontSize = text.FontSize,
                    Foreground = new SolidColorBrush(Color.FromUInt32(text.Color))
                };
                Canvas.SetLeft(label, text.Origin.X);
                Canvas.SetTop(label, text.Origin.Y - text.FontSize);
                NoteLayer.Children.Add(label);
                break;

            case NumberAnnotation number:
                var circle = new Ellipse
                {
                    Width = number.Size,
                    Height = number.Size,
                    Fill = new SolidColorBrush(Color.FromUInt32(number.Color))
                };
                Canvas.SetLeft(circle, number.Center.X - number.Size / 2);
                Canvas.SetTop(circle, number.Center.Y - number.Size / 2);
                NoteLayer.Children.Add(circle);

                var badge = new TextBlock
                {
                    Text = number.Number.ToString(),
                    FontSize = number.Size * 0.55,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White
                };
                Canvas.SetLeft(badge, number.Center.X - number.Size * 0.14);
                Canvas.SetTop(badge, number.Center.Y - number.Size * 0.34);
                NoteLayer.Children.Add(badge);
                break;
        }
    }

    // ---------- 文字输入 ----------

    private void ShowTextEditor(Point origin)
    {
        _textOrigin = origin;
        TextNoteBox.Text = string.Empty;
        Canvas.SetLeft(TextNoteBox, _selection.X + origin.X);
        Canvas.SetTop(TextNoteBox, _selection.Y + origin.Y);
        TextNoteBox.IsVisible = true;
        TextNoteBox.Focus();
    }

    private void HideTextEditor()
    {
        TextNoteBox.IsVisible = false;
        TextNoteBox.Text = string.Empty;
    }

    private void CommitTextNote()
    {
        if (!TextNoteBox.IsVisible)
            return;

        var text = TextNoteBox.Text?.Trim() ?? string.Empty;
        var origin = _textOrigin;
        HideTextEditor();

        if (text.Length == 0)
            return;

        // 编辑器左上角对齐，Skia 的 DrawText 用基线：往下让一个字高
        _notes.Add(new TextAnnotation(
            new Point(origin.X, origin.Y + TextFontSize),
            text,
            _noteColor.ToUInt32(),
            TextFontSize));

        RebuildNotePreview();
        UpdateToolButtons();
    }

    private void OnTextNoteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTextNote();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideTextEditor();
            e.Handled = true;
        }
    }

    private void OnTextNoteLostFocus(object? sender, RoutedEventArgs e) => CommitTextNote();

    private void AddPreviewLine(IBrush brush, Point from, Point to, double width) =>
        NoteLayer.Children.Add(new Line
        {
            StartPoint = from,
            EndPoint = to,
            Stroke = brush,
            StrokeThickness = width,
            StrokeLineCap = PenLineCap.Round
        });

    private Rect ToDip(PixelRect bounds) => new(
        (bounds.X - _origin.X) / _scaling,
        (bounds.Y - _origin.Y) / _scaling,
        bounds.Width / _scaling,
        bounds.Height / _scaling);

    /// <summary>选区所在的显示器（DIP，相对截图窗口）；无头测试或取不到时返回 null。</summary>
    private Rect? ActiveScreenDip()
    {
        var center = new PixelPoint(
            _origin.X + (int)Math.Round((_selectionOrigin.X + _selectionOrigin.Width / 2) * _scaling),
            _origin.Y + (int)Math.Round((_selectionOrigin.Y + _selectionOrigin.Height / 2) * _scaling));

        try
        {
            return Screens.ScreenFromPoint(center) is { } screen
                ? ToDip(screen.Bounds)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>导出前的合成：先画标注，再按需切圆角。</summary>
    private byte[] ComposeResult(byte[] png)
    {
        var composed = AnnotationRenderer.Compose(png, _notes.Items, _scaling);
        return AnnotationRenderer.RoundCorners(composed, _cornerRadius * _scaling);
    }

    private void OnSizeBadgeClick(object? sender, PointerPressedEventArgs e)
    {
        ToggleSizeUnit();
        e.Handled = true;
    }

    internal void ToggleSizeUnit()
    {
        _sizeInPixels = !_sizeInPixels;
        UpdateSizeBadge(_selection);
    }

    // ---------- 样式模板（三槽） ----------

    /// <summary>三槽样式模板；由宿主在窗口显示前灌入，关闭后读回持久化。</summary>
    public AnnotationPreset?[] Presets { get; } = new AnnotationPreset?[3];

    internal void SavePreset(int index)
    {
        if (index < 0 || index >= Presets.Length)
            return;

        Presets[index] = new AnnotationPreset(
            $"槽 {index + 1}",
            $"#{_noteColor.R:X2}{_noteColor.G:X2}{_noteColor.B:X2}",
            _noteWidth,
            _mosaicBlur,
            _cornerRadius);

        BuildPresetMenu();
    }

    internal void ApplyPreset(int index)
    {
        if (index < 0 || index >= Presets.Length || Presets[index] is not { } preset)
            return;

        _noteColor = Color.Parse(preset.Color);
        _noteWidth = preset.Width;
        _mosaicBlur = preset.MosaicBlur;
        _cornerRadius = preset.CornerRadius;
        UpdateStyleButtons();
    }

    internal void ClearPreset(int index)
    {
        if (index < 0 || index >= Presets.Length)
            return;

        Presets[index] = null;
        BuildPresetMenu();
    }

    /// <summary>「模板」菜单按当前三槽重建：有内容的槽多出「应用 / 清除」。</summary>
    internal void BuildPresetMenu()
    {
        if (PresetButton.Flyout is not MenuFlyout menu)
            return;

        menu.Items.Clear();

        for (var index = 0; index < Presets.Length; index++)
        {
            if (Presets[index] is not { } preset)
                continue;

            var slot = index;
            var apply = new MenuItem { Header = $"应用 {preset.Name}" };
            apply.Click += (_, _) => ApplyPreset(slot);
            menu.Items.Add(apply);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());

        for (var index = 0; index < Presets.Length; index++)
        {
            var slot = index;
            var save = new MenuItem
            {
                Header = Presets[index] is null ? $"保存到槽 {index + 1}" : $"覆盖槽 {index + 1}"
            };
            save.Click += (_, _) => SavePreset(slot);
            menu.Items.Add(save);
        }

        for (var index = 0; index < Presets.Length; index++)
        {
            if (Presets[index] is null)
                continue;

            var slot = index;
            var clear = new MenuItem { Header = $"清除槽 {index + 1}" };
            clear.Click += (_, _) => ClearPreset(slot);
            menu.Items.Add(clear);
        }
    }

    private void ShowGuides(double? x, double? y)
    {
        GuideLayer.Children.Clear();

        if (x is null && y is null)
        {
            GuideLayer.IsVisible = false;
            return;
        }

        var brush = new SolidColorBrush(Color.Parse("#B4E5A44C"));

        if (x is { } vertical)
        {
            GuideLayer.Children.Add(new Line
            {
                StartPoint = new Point(vertical, 0),
                EndPoint = new Point(vertical, Height),
                Stroke = brush,
                StrokeThickness = 1,
                StrokeDashArray = [4, 3]
            });
        }

        if (y is { } horizontal)
        {
            GuideLayer.Children.Add(new Line
            {
                StartPoint = new Point(0, horizontal),
                EndPoint = new Point(Width, horizontal),
                Stroke = brush,
                StrokeThickness = 1,
                StrokeDashArray = [4, 3]
            });
        }

        GuideLayer.IsVisible = true;
    }

    private void ClearGuides()
    {
        GuideLayer.Children.Clear();
        GuideLayer.IsVisible = false;
    }

    private void OnToolbarDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _draggingToolbar = true;
        _toolbarManual = true;
        _toolbarDragStart = e.GetPosition(this);

        ActionBar.Measure(Size.Infinity);
        _toolbarStartPosition = new Point(
            Canvas.GetLeft(ActionBar),
            Canvas.GetTop(ActionBar));

        e.Handled = true;
    }

    private void MoveToolbar(Point point)
    {
        var size = ActionBar.Bounds.Width > 0 ? ActionBar.Bounds.Size : ActionBar.DesiredSize;

        var left = Math.Clamp(
            _toolbarStartPosition.X + (point.X - _toolbarDragStart.X),
            8,
            Math.Max(8, Width - size.Width - 8));
        var top = Math.Clamp(
            _toolbarStartPosition.Y + (point.Y - _toolbarDragStart.Y),
            8,
            Math.Max(8, Height - size.Height - 8));

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, top);
    }

    private void ShowActionBar(Rect selection)
    {
        ActionBar.IsVisible = true;
        PositionActionBar(selection);
    }

    /// <summary>
    /// 工具条位置：对齐选区右下角，放得下就放下方，放不下翻到上方，
    /// 最后横向/纵向钳制在窗口内（参考 snow-shot 的 anchoredToolbarPlacement）。
    /// 用实测尺寸而不是估算宽度——按钮数量一变估算就失效。
    /// </summary>
    private bool _compactToolbar;

    /// <summary>窗口窄于这个宽度（DIP）就把低频动作收起来。</summary>
    internal static bool ShouldCompact(double width) => width > 0 && width < 900;

    /// <summary>
    /// 窄窗口把低频动作收进「更多」。
    /// 学 snow-shot 的 layout compaction，只是规则更简单（按窗口宽度，而不是逐项量宽）。
    /// </summary>
    private void ApplyToolbarCompaction() => SetToolbarCompaction(ShouldCompact(Width));

    internal void SetToolbarCompaction(bool compact)
    {
        if (compact == _compactToolbar)
            return;

        _compactToolbar = compact;

        CopyImageButton.IsVisible = !compact;
        SaveButton.IsVisible = !compact;
        PinButton.IsVisible = !compact;
        ScrollButton.IsVisible = !compact;
        MoreButton.IsVisible = compact;
    }

    private void PositionActionBar(Rect selection)
    {
        if (!ActionBar.IsVisible)
            return;

        ApplyToolbarCompaction();

        // 用户拖过工具条之后就不再自动跟随选区
        if (_toolbarManual)
            return;

        ActionBar.Measure(Size.Infinity);
        var size = ActionBar.DesiredSize;

        var position = ComputeToolbarPosition(selection, size, new Size(Width, Height));
        Canvas.SetLeft(ActionBar, position.X);
        Canvas.SetTop(ActionBar, position.Y);
    }

    internal static Point ComputeToolbarPosition(
        Rect selection,
        Size toolbar,
        Size bounds,
        double gap = 8,
        double margin = 8)
    {
        // 首选：右边缘对齐选区右边缘，放在下方
        var left = selection.Right - toolbar.Width;
        var top = selection.Bottom + gap;

        // 下方放不下就翻到上方（snow-shot 的 bottom-first 规则）
        if (top + toolbar.Height > bounds.Height - margin)
            top = selection.Y - toolbar.Height - gap;

        left = Math.Clamp(left, margin, Math.Max(margin, bounds.Width - toolbar.Width - margin));
        top = Math.Clamp(top, margin, Math.Max(margin, bounds.Height - toolbar.Height - margin));

        return new Point(left, top);
    }

    /// <summary>结果卡片：优先选区右侧，其次左侧，最后钳制到窗口内。</summary>
    internal static Point ComputeCardPosition(
        Rect selection,
        Size card,
        Size bounds,
        double gap = 12,
        double margin = 8)
    {
        var left = selection.Right + gap;

        if (left + card.Width > bounds.Width - margin)
            left = selection.X - card.Width - gap;

        left = Math.Clamp(left, margin, Math.Max(margin, bounds.Width - card.Width - margin));
        var top = Math.Clamp(
            selection.Y,
            margin,
            Math.Max(margin, bounds.Height - card.Height - margin));

        return new Point(left, top);
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

            // 标注合成进导出的图：复制 / 保存 / 固定 / 识别拿到的都是带标注的版本
            Result = action == CaptureAction.Scroll
                ? png
                : await Task.Run(() => ComposeResult(png));

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

    private async Task RunOcrAsync(CaptureOcrOptions? options = null, bool showText = false)
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
        TranslateButton.IsEnabled = false;
        OcrStatus.IsVisible = true;
        OcrStatus.Text = options?.TranslateTarget is { Length: > 0 } target
            ? $"正在翻译（{target}）…"
            : "正在识别…";

        try
        {
            var result = await OcrRunner(png, options ?? new CaptureOcrOptions(), _ocrCancellation.Token);

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

            if (showText && !string.IsNullOrWhiteSpace(result.Text))
                ShowResultCard(result.Text);
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
            TranslateButton.IsEnabled = true;
            _ocrCancellation?.Dispose();
            _ocrCancellation = null;
        }
    }

    /// <summary>
    /// 「翻译」菜单选中语言后：先按当前选区取图，再走同一条 OCR 链路，只是多带目标语言。
    /// 不能直接调 RunOcrAsync——那时 Result 还没生成。
    /// </summary>
    private async Task RunTranslateAsync(string target)
    {
        if (_isActing || _isRecognizing || !_hasSelection)
            return;

        if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            return;

        _isActing = true;

        try
        {
            var png = await CaptureSelectionAsync(CaptureAction.Recognize);
            if (png is null)
            {
                Action = CaptureAction.None;
                Close();
                return;
            }

            if (!IsVisible)
                return;

            Result = await Task.Run(() => ComposeResult(png));
            Action = CaptureAction.Recognize;

            await RunOcrAsync(new CaptureOcrOptions(target), showText: true);
        }
        finally
        {
            _isActing = false;
        }
    }

    /// <summary>把结果卡片放在选区旁边：优先右侧，放不下就左侧和下方。</summary>
    internal void ShowResultCard(string text)
    {
        ResultText.Text = text;
        ResultCard.IsVisible = true;

        ResultCard.Measure(Size.Infinity);
        var size = ResultCard.DesiredSize;

        var position = ComputeCardPosition(_selection, size, new Size(Width, Height));
        Canvas.SetLeft(ResultCard, position.X);
        Canvas.SetTop(ResultCard, position.Y);
    }

    private void HideResultCard() => ResultCard.IsVisible = false;

    private void OnResultCloseClick(object? sender, RoutedEventArgs e) => HideResultCard();

    private void OnResultCopyClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ResultText.Text))
            CopyText?.Invoke(ResultText.Text);
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
        HideResultCard();
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
            hit.Stroke = LineHoverStroke;
            hit.StrokeThickness = 2;
        }
    }

    private void OnOcrLayerPointerExited(object? sender, PointerEventArgs e)
    {
        if (_hoveredOcrBox is null)
            return;

        _hoveredOcrBox.IsVisible = false;
        _hoveredOcrBox.Fill = LineFill;
        _hoveredOcrBox.Stroke = LineStroke;
        _hoveredOcrBox.StrokeThickness = 1;
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
        // 悬停只加亮边框，不再用填充色——中间要保持原图可见
        if (sender is Rectangle box)
        {
            box.Stroke = LineHoverStroke;
            box.StrokeThickness = 2;
        }
    }

    private void OnOcrLineExited(object? sender, PointerEventArgs e)
    {
        if (sender is Rectangle box)
        {
            box.Stroke = LineStroke;
            box.StrokeThickness = 1;
        }
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

        // Ctrl+Z：撤销最后一条标注
        if (key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            UndoAnnotation();
            e.Handled = true;
            return;
        }

        // 数字键 2..9 切标注工具（和按钮提示一致）
        if (e.KeyModifiers == KeyModifiers.None && TryMapToolKey(key, out var tool)
            && _hasSelection)
        {
            SetTool(tool);
            e.Handled = true;
            return;
        }

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

        SizeText.Text = _sizeInPixels
            ? $"{x}, {y}  {width} × {height}"
            : $"{x}, {y}  {rect.Width:0} × {rect.Height:0} DIP";

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
