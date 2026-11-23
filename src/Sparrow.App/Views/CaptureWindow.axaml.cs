using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
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
using Sparrow.Plugin.Sdk.Payloads;
using SkiaSharp;

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
        Resize,

        /// <summary>拖动编辑已有标注。</summary>
        NoteEdit,

        /// <summary>拖动选中标注的缩放控制点。</summary>
        NoteResize,

        /// <summary>拖动旋转手柄。</summary>
        NoteRotate
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
    // OCR 行框：常态红框 + 淡红底（参考 OmniSeeker 的识别块），
    // 选中改琥珀，避免在深色截图里再引入第二种强调色
    private static readonly IBrush LineFill = new SolidColorBrush(Color.Parse("#1FFF5F5F"));
    private static readonly IBrush LineStroke = new SolidColorBrush(Color.Parse("#FF5F5F"));
    private static readonly IBrush LineHoverStroke = new SolidColorBrush(Color.Parse("#FFE5A44C"));
    private static readonly IBrush LineHoverFill = new SolidColorBrush(Color.Parse("#33FF5F5F"));
    private static readonly IBrush LineSelectedFill = new SolidColorBrush(Color.Parse("#1FE5A44C"));

    /// <summary>字符级选区的底色：比行框选中更重，方便和"整行选中"区分。</summary>
    private static readonly IBrush OcrCharSelectionFill = new SolidColorBrush(Color.Parse("#4DE5A44C"));
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IBrush HandleStroke = new SolidColorBrush(Color.Parse("#B3000000"));

    // 放大镜：显示在光标附近的像素级预览
    private const double MagnifierSize = 132;
    private const double MagnifierZoom = 4;

    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor IbeamCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    internal static Cursor CrossCursorValue => CrossCursor;
    internal static Cursor MoveCursorValue => MoveCursor;
    internal static Cursor IbeamCursorValue => IbeamCursor;
    private static readonly Cursor NorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor WestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor NwseCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor NeswCursor = new(StandardCursorType.TopRightCorner);

    private readonly Dictionary<ResizeHandle, Rectangle> _handles = [];

    private PixelPoint _origin;
    private double _scaling = 1;
    private int _virtualWidth;
    private int _virtualHeight;
    private byte[]? _frozenPixels;
    private int _frozenWidth;
    private int _frozenHeight;
    private WriteableBitmap? _frozenBitmap;
    /// <summary>
    /// 识别框显示方式。默认「全部显示」——识别完就能看到每一块文字在哪，
    /// 和 OmniSeeker 的识别结果页一致；B 键可以切到悬停 / 关闭。
    /// </summary>
    private OcrBoxMode _ocrBoxMode = OcrBoxMode.All;
    private readonly List<Rectangle> _ocrBoxes = [];
    private readonly HashSet<Rectangle> _selectedOcrBoxes = [];

    /// <summary>OCR 行（DIP）+ 每行的字符框，字符级选择用。</summary>
    private readonly List<(OcrTextLine Line, IReadOnlyList<OcrTextLayout.OcrCharBox> Chars)> _ocrLines = [];

    /// <summary>已经打上的文字标记（烧进导出图）。</summary>
    private readonly List<OcrTextMark> _ocrMarks = [];

    /// <summary>译文图（PNG）；非空表示这次识别带了翻译。</summary>
    private byte[]? _translatedPng;

    /// <summary>当前是否在"显示原图"（默认显示译文）。</summary>
    private bool _showOriginalView;

    /// <summary>本次识别的翻译目标；为空表示只识别。</summary>
    private string? _translateTarget;

    private (int Line, int Char)? _ocrSelAnchor;
    private (int Line, int Char)? _ocrSelFocus;
    private bool _ocrDragging;
    private int _ocrPressClickCount = 1;
    private Point _ocrPressPoint;

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
    private Rect _candidateDip;
    private Rect _selectionOrigin;
    private Point _dragOrigin;
    private Point _createStart;
    private bool _createConstrained;
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

    // 标注的选中 / 拖动编辑：索引指向 _notes.Items
    private int _selectedNote = -1;
    private int _draggingNote = -1;
    private Point _noteDragStart;
    private IAnnotation? _noteDragOriginal;
    private bool _noteDragMoved;

    private const double MinNoteSize = 12;
    private const double RotateHandleDistance = 18;
    private ResizeHandle _noteResizeHandle;
    private IAnnotation? _noteResizeOriginal;
    private Rect _noteResizeBounds;
    private int _noteResizeIndex = -1;
    private bool _noteResizeMoved;

    private int _noteRotateIndex = -1;
    private IAnnotation? _noteRotateOriginal;
    private double _noteRotateBase;
    private Point _noteRotateCenter;
    private Point _noteRotateStartVector;
    private bool _noteRotateMoved;

    /// <summary>移动/拉伸选区前的标注快照；每帧从快照平移，避免累积误差。</summary>
    private List<IAnnotation>? _notesBeforeDrag;

    private sealed record SelectionSnapshot(Rect Selection, IReadOnlyList<IAnnotation> Notes);

    private readonly Stack<SelectionSnapshot> _selectionUndo = new();
    private readonly Stack<SelectionSnapshot> _selectionRedo = new();

    private static readonly Color NoteColor = Color.Parse("#E5A44C");
    private const double NoteWidth = 3;
    private const double HighlighterWidth = 18;
    private const double HighlighterOpacity = 0.4;
    private const int MosaicCell = 12;
    private const double TextFontSize = 16;
    private const double NumberSize = 24;

    // 当前画笔样式（属性栏驱动，只影响之后画的图元）
    private double _noteOpacity = 1;
    private ShapeFill _noteFill = ShapeFill.None;
    private double _noteRadius;
    private LineDash _noteDash = LineDash.Solid;
    private double _noteFontSize = TextFontSize;
    private bool _noteBold;
    private int _mosaicCell = MosaicCell;
    private double _numberSize = NumberSize;

    private int _nextNumber = 1;
    private Point _textOrigin;

    // 文本编辑目标：新建文字，或改写已有水印/气泡（后续工具）
    private enum TextEditTarget
    {
        NewText,
        Note
    }

    private TextEditTarget _textEditTarget = TextEditTarget.NewText;
    private int _textEditNoteIndex = -1;

    // 水印默认样式（属性栏只暴露颜色 / 字号 / 透明度）
    private string _watermarkText = "Sparrow";
    private double _watermarkFontSize = 32;
    private double _watermarkOpacity = 0.35;
    private const double WatermarkAngle = 30;
    private const double WatermarkSpacing = 160;

    // 气泡样式
    private double _bubbleFontSize = TextFontSize;
    private ShapeFill _bubbleFill = ShapeFill.None;

    // 工具条拖动：手动位置之后不再跟随选区
    private bool _draggingToolbar;
    private bool _toolbarManual;
    private bool _toolbarPlacementApplied;
    private Point? _toolbarFraction;
    private Point _toolbarDragStart;
    private Point _toolbarStartPosition;

    /// <summary>选区覆盖的那个窗口（DIP）；拖动/拉伸时吸附它的边和中线。</summary>
    private Rect? _snapBox;

    /// <summary>拖动开始时算一次选区所在显示器（DIP）。</summary>
    private Rect? _activeScreen;

    /// <summary>尺寸角标显示物理像素还是 DIP。</summary>
    private bool _sizeInPixels = true;

    /// <summary>锁定宽高比。</summary>
    private bool _aspectLocked;

    /// <summary>宽高比预设（0 = 自由）。选了预设就按它约束拖建/拉边/输入三条路径。</summary>
    private double _ratioPreset;

    /// <summary>按在选区边框上时被临时让位的绘图工具；松手后恢复。</summary>
    private AnnotationTool? _toolBeforeResize;

    /// <summary>回填输入框期间不触发提交，避免拖动选区时反复换算。</summary>
    private bool _sizeEditGuard;

    /// <summary>导出圆角半径（0 = 不切）。</summary>
    private double _cornerRadius;

    /// <summary>取色模式：光标处显示色值，点击复制并写回标注颜色。</summary>
    private bool _colorSampling;

    /// <summary>取色格式：0 = HEX，1 = RGB，2 = HSL（Shift+I 循环）。</summary>
    private int _colorFormat;

    // 橡皮擦正在拖动擦除。
    private bool _erasing;

    /// <summary>水印/气泡工具按在已有同类标注上：松手不新建，避免双击时先叠一层。</summary>
    private bool _notePressOnExisting;

    /// <summary>角度工具的两步输入阶段。</summary>
    private enum AnglePhase
    {
        None,
        First,
        Second
    }

    private readonly AngleDraftState _angleDraft = new();
    private AnglePhase _anglePhase;

    /// <summary>聚光灯的暗度（0..1），和标注透明度分开存。</summary>
    private double _spotlightOpacity = 0.55;

    public double ExportCornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(0, value);
            if (_hasSelection)
                UpdateSelectionVisuals();
        }
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
        BuildToolGroups();
        RenderToolbar();

        OcrLayer.PointerMoved += OnOcrLayerPointerMoved;
        OcrLayer.PointerExited += OnOcrLayerPointerExited;
        OcrLayer.PointerPressed += OnOcrLayerPressed;
        OcrLayer.PointerReleased += OnOcrLayerReleased;
        UpdateStyleButtons();
        UpdateToolButtons();

        // 属性行显隐 / OCR 状态文字都会改变工具条宽度：
        // 布局完成后再校正一次居中（Bounds 才是最终尺寸，DesiredSize 可能还是上一帧的）
        ActionBar.LayoutUpdated += OnActionBarLayoutUpdated;

        // 自定义颜色的历史色：先从主题色起步
        _recentColors.AddRange(
        [
            Color.Parse("#E5A44C"),
            Color.Parse("#FFFFFF"),
            Color.Parse("#FF5F5F"),
            Color.Parse("#5FD08C"),
            Color.Parse("#5FA8FF")
        ]);
        BuildRecentColors();
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

    /// <summary>
    /// 工具条手动位置：相对当前显示器的 0–1 比例 [x, y]；空 = 跟随选区。
    /// 下次截图恢复到这个位置；选区移动后仍恢复为跟随。
    /// </summary>
    public IReadOnlyList<double> CaptureToolbarPlacement
    {
        get => _toolbarFraction is { } fraction
            ? [Math.Round(fraction.X, 4), Math.Round(fraction.Y, 4)]
            : [];

        set
        {
            if (value is { Count: >= 2 }
                && value[0] is >= 0 and <= 1
                && value[1] is >= 0 and <= 1)
            {
                _toolbarFraction = new Point(value[0], value[1]);
                _toolbarManual = true;
                _toolbarPlacementApplied = false;
                return;
            }

            _toolbarFraction = null;
            _toolbarManual = false;
            _toolbarPlacementApplied = false;
        }
    }

    /// <summary>工具条布局；宿主读写持久化。空列表回落默认。</summary>
    public IReadOnlyList<string> CaptureToolPositions
    {
        get => _layout.SerializePositions();
        set => ApplyToolbarLayout(ToolbarLayout.Parse(value, _layout.SerializeHidden()));
    }

    /// <summary>被隐藏的工具 id；宿主读写持久化。</summary>
    public IReadOnlyList<string> CaptureToolHidden
    {
        get => _layout.SerializeHidden();
        set => ApplyToolbarLayout(ToolbarLayout.Parse(_layout.SerializePositions(), value));
    }

    private void CreateHandles()
    {
        foreach (var handle in new[]
                 {
                     ResizeHandle.NW, ResizeHandle.N, ResizeHandle.NE, ResizeHandle.E,
                     ResizeHandle.SE, ResizeHandle.S, ResizeHandle.SW, ResizeHandle.W
                 })
        {
            // 小方块控制点：白底 + 深色描边，参考 LumaShot 的角点样式
            var dot = new Rectangle
            {
                Width = 7,
                Height = 7,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = HandleFill,
                Stroke = HandleStroke,
                StrokeThickness = 1,
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
            _translateAvailable = false;
            TranslateButton.IsVisible = false;
            InvalidateToolbarMetrics();
            return;
        }

        menu.Items.Clear();

        foreach (var (value, label) in targets)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await RunTranslateAsync(value);
            menu.Items.Add(item);
        }

        _translateAvailable = menu.Items.Count > 0;
        TranslateButton.IsVisible = _translateAvailable && _toolbarDensity < 2;
        InvalidateToolbarMetrics();
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
        _groupShowTimer?.Stop();
        _groupHideTimer?.Stop();

        var releasedBytes = (_frozenPixels?.Length ?? 0)
            + (long)_frozenWidth * _frozenHeight * 4;

        FrozenFrame.Source = null;
        _frozenBitmap?.Dispose();
        _frozenBitmap = null;
        _frozenPixels = null;

        TrimFreezeMemory(releasedBytes);

        base.OnClosed(e);
    }

    /// <summary>
    /// 冻结帧是整屏大对象（4K 约 33MB）。抓图会把 LOH 高水位顶上去，
    /// 关窗后在后台压缩一次，把这段内存还给系统，不卡 UI。
    /// </summary>
    private static void TrimFreezeMemory(long releasedBytes)
    {
        // 小屏/测试没必要付这次 GC；释放过大块冻结帧时即使总内存不高也压缩一次
        if (releasedBytes < 16L * 1024 * 1024
            && Environment.WorkingSet < 600L * 1024 * 1024)
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

        // Name=HintBar 是 Canvas 子项：居中要自己算，Alignment 在 Canvas 里不生效
        HintBar.Measure(Size.Infinity);
        Canvas.SetLeft(HintBar, Math.Max(8, (Width - HintBar.DesiredSize.Width) / 2));
        UpdateDim();
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

        // 右键逐级退出（参考 snow-shot / LumaShot 的手感）
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            HandleRightClick();
            e.Handled = true;
            return;
        }

        // 自定义颜色面板：点里面交给控件，点外面先收起
        if (CustomColorPanel.IsVisible)
        {
            if (IsPointInCustomColorPanel(point))
                return;

            CustomColorPanel.IsVisible = false;
        }

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

        // 点空白处收起工具组选项条；点选项条本身要放过去，否则按钮在按下时就没了
        if (ToolGroupPopover.IsVisible)
        {
            var insidePopover =
                ToolGroupPopover.TranslatePoint(new Point(0, 0), this) is { } popoverOrigin
                && new Rect(popoverOrigin, ToolGroupPopover.Bounds.Size).Contains(point);

            if (insidePopover)
                return;

            HideToolGroupPopover();
        }

        // 取色模式：左键取样并退出，不参与框选
        if (_colorSampling)
        {
            CommitColorSample();
            e.Handled = true;
            return;
        }

        // 角度：两次拖拽；草稿存在时第二段优先于边框拉伸
        if (_tool == AnnotationTool.Angle && _hasSelection && _selection.Contains(point))
        {
            BeginAngleDrag(point);
            e.Handled = true;
            return;
        }

        // 边框带优先于当前工具：画着标注时按在边框 8 内也能拉选区（参考 snow-shot），
        // 松手后把工具恢复回去
        _toolBeforeResize = null;
        if (_tool != AnnotationTool.None && _hasSelection
            && HitHandle(point) != ResizeHandle.None)
        {
            _toolBeforeResize = _tool;
            ForceTool(AnnotationTool.None);
        }

        // 水印 / 气泡：按在已有同类标注上先不新建（双击改文字，单击只选中）
        _notePressOnExisting = false;
        if (_tool is AnnotationTool.Watermark or AnnotationTool.Bubble && _hasSelection)
        {
            var hit = HitNote(point);
            var sameKind = hit >= 0 && _notes.Items[hit] switch
            {
                WatermarkAnnotation when _tool == AnnotationTool.Watermark => true,
                SpeechBubbleAnnotation when _tool == AnnotationTool.Bubble => true,
                _ => false
            };

            if (sameKind)
            {
                if (e.ClickCount == 2)
                    EditNoteText(hit);
                else
                    SelectNote(hit);

                _notePressOnExisting = e.ClickCount != 2;
                e.Handled = true;
                return;
            }
        }

        // 选了标注工具：在选区内按下就是开始画，不移动选区
        if (BeginAnnotation(point))
        {
            e.Handled = true;
            return;
        }

        // 橡皮擦：按下擦掉指针下最上面的一条，拖动连续擦
        if (_tool == AnnotationTool.Eraser)
        {
            if (_hasSelection && _selection.Contains(point))
            {
                _erasing = true;
                EraseAt(point);
            }

            e.Handled = true;
            return;
        }

        // 指针工具：先看点没点中已有标注（点中就拖动它），否则继续移动选区
        if (_tool == AnnotationTool.None && _hasSelection && _selection.Contains(point))
        {
            if (HitNoteRotateHandle(point))
            {
                BeginNoteRotate(point);
                e.Handled = true;
                return;
            }

            var noteHandle = HitNoteHandle(point);
            if (noteHandle != ResizeHandle.None)
            {
                BeginNoteResize(point, noteHandle);
                e.Handled = true;
                return;
            }

            var hit = HitNote(point);
            if (hit >= 0)
            {
                SelectNote(hit);
                _draggingNote = hit;
                _noteDragOriginal = _notes.Items[hit];
                _noteDragStart = new Point(point.X - _selection.X, point.Y - _selection.Y);
                _noteDragMoved = false;
                _mode = DragMode.NoteEdit;
                Cursor = MoveCursor;
                e.Handled = true;
                return;
            }

            SelectNote(-1);   // 点空白：先取消选中，再走原来的移动/拉伸
        }

        // 识别结果卡片 / 文字标记菜单：点它们只是操作卡片，不能当成"点空白清空识别"
        if (IsPointInControl(ResultCard, point) || IsPointInControl(OcrMarkMenu, point))
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
                _activeScreen = ActiveScreenDip();
                _notesBeforeDrag = _notes.Items.ToList();
                HideTextEditor();
                e.Handled = true;
                return;
            }

            if (_selection.Contains(point))
            {
                _mode = DragMode.Move;
                _dragOrigin = point;
                _selectionOrigin = _selection;
                _activeScreen = ActiveScreenDip();
                _notesBeforeDrag = _notes.Items.ToList();
                HideTextEditor();
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

        // 取色模式：光标处只更新探针，不做别的
        if (_colorSampling)
        {
            UpdateColorProbe(point);
            e.Handled = true;
            return;
        }

        // 橡皮擦拖动：连续擦掉指针下的标注
        if (_erasing)
        {
            EraseAt(point);
            e.Handled = true;
            return;
        }

        // 拖动工具条：优先于选区/标注
        if (_draggingToolbar)
        {
            MoveToolbar(point);
            Magnifier.IsVisible = false;
            CrosshairLayer.IsVisible = false;
            e.Handled = true;
            return;
        }

        // 正在画标注：更新预览，不参与移动/拉伸选区
        if (_pendingAnnotation is not null)
        {
            var target = point;

            // 测距按住 Shift：方向吸附到 45° 的整数倍
            if (_pendingAnnotation is DistanceAnnotation &&
                e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                var origin = new Point(
                    _selection.X + _annotationStart.X,
                    _selection.Y + _annotationStart.Y);
                target = AnnotationGeometry.SnapToAngle(origin, point);
            }

            UpdatePendingAnnotation(target);
            RebuildNotePreview();
            Magnifier.IsVisible = false;
            CrosshairLayer.IsVisible = false;
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

        UpdateCrosshair(point);

        switch (_mode)
        {
            case DragMode.Create:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _createConstrained = true;

                ApplyCreate(point, _createConstrained);
                UpdateMagnifier(point);
                e.Handled = true;
                return;
            case DragMode.Move:
                ApplyMove(point);
                UpdateMagnifier(point);
                e.Handled = true;
                return;
            case DragMode.Resize:
                ApplyResize(point);
                UpdateMagnifier(point);
                e.Handled = true;
                return;
            case DragMode.NoteEdit:
                ApplyNoteDrag(point);
                Magnifier.IsVisible = false;
                e.Handled = true;
                return;
            case DragMode.NoteResize:
                ApplyNoteResize(point);
                Magnifier.IsVisible = false;
                e.Handled = true;
                return;
            case DragMode.NoteRotate:
                ApplyNoteRotate(point, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                Magnifier.IsVisible = false;
                e.Handled = true;
                return;
        }

        Magnifier.IsVisible = false;
        UpdateCursor(point);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_draggingToolbar)
        {
            _draggingToolbar = false;
            if (_hasSelection)
                RememberToolbarPlacement();

            e.Handled = true;
            return;
        }

        if (_erasing)
        {
            _erasing = false;
            e.Handled = true;
            return;
        }

        // 按在已有水印上松手：本次不新建
        if (_notePressOnExisting)
        {
            _notePressOnExisting = false;
            e.Handled = true;
            return;
        }

        // 画完一条标注：小于阈值当误触丢掉
        if (_pendingAnnotation is not null)
        {
            if (_tool == AnnotationTool.Angle)
                CommitAngleDrag();
            else
                CommitPendingAnnotation();

            ClearGuides();
            e.Handled = true;
            return;
        }

        ClearGuides();

        // 拖动编辑结束：标注已经落在新位置，保持选中方便继续微调 / 删除
        if (_mode == DragMode.NoteEdit)
        {
            if (_noteDragMoved
                && _draggingNote >= 0
                && _noteDragOriginal is { } original
                && _draggingNote < _notes.Items.Count)
            {
                _notes.RecordReplace(_draggingNote, original, _notes.Items[_draggingNote]);
            }

            _mode = DragMode.None;
            _draggingNote = -1;
            _noteDragOriginal = null;
            _noteDragMoved = false;
            RebuildNotePreview();
            e.Handled = true;
            return;
        }

        if (_mode == DragMode.NoteResize)
        {
            if (_noteResizeMoved
                && _noteResizeIndex >= 0
                && _noteResizeOriginal is { } original
                && _noteResizeIndex < _notes.Items.Count)
            {
                _notes.RecordReplace(_noteResizeIndex, original, _notes.Items[_noteResizeIndex]);
            }

            _mode = DragMode.None;
            _noteResizeIndex = -1;
            _noteResizeOriginal = null;
            _noteResizeMoved = false;
            RebuildNotePreview();
            e.Handled = true;
            return;
        }

        if (_mode == DragMode.NoteRotate)
        {
            if (_noteRotateMoved
                && _noteRotateIndex >= 0
                && _noteRotateOriginal is { } original
                && _noteRotateIndex < _notes.Items.Count)
            {
                _notes.RecordReplace(_noteRotateIndex, original, _notes.Items[_noteRotateIndex]);
            }

            _mode = DragMode.None;
            _noteRotateIndex = -1;
            _noteRotateOriginal = null;
            _noteRotateMoved = false;
            RebuildNotePreview();
            e.Handled = true;
            return;
        }

        if (_mode == DragMode.None)
            return;

        var mode = _mode;
        _mode = DragMode.None;
        Magnifier.IsVisible = false;

        var candidateAtPress = _candidateAtPress;
        var uiCandidateAtPress = _uiCandidateAtPress;
        _candidateAtPress = null;
        _uiCandidateAtPress = null;

        if (mode == DragMode.Create)
        {
            ApplyCreate(
                e.GetPosition(this),
                _createConstrained || e.KeyModifiers.HasFlag(KeyModifiers.Shift));

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

            // 选区已经定下来了，候选框和 UIA 树都不会再用：现在就把上千个
            // COM 节点还掉，别一直压到截图窗口关闭
            _uiProbe?.ReleaseTrees();
        }
        else
        {
            // 移动/拉伸结束：动作栏跟着新位置走
            UpdateSizeBadge(_selection);
            PositionActionBar(_selection);
            UpdateHandles();

            // 边框临时让位的绘图工具在这里恢复
            if (_toolBeforeResize is { } previous)
            {
                _toolBeforeResize = null;
                ForceTool(previous);
            }

            // 几何真的变了才进撤销栈；快照是拖动开始前的选区 + 标注
            if (_notesBeforeDrag is { } notesBefore && _selection != _selectionOrigin)
            {
                _selectionUndo.Push(new SelectionSnapshot(_selectionOrigin, notesBefore));
                _selectionRedo.Clear();
                UpdateToolButtons();
            }
        }

        _notesBeforeDrag = null;
        e.Handled = true;
    }

    private void BeginCreate(Point point)
    {
        _hasSelection = false;
        _mode = DragMode.Create;
        _createStart = point;
        _createConstrained = false;
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
        OcrBoxButton.IsVisible = false;
        _ocrActionsAvailable = false;
        HintBar.IsVisible = false;
        HideResultCard();
        _notes.Clear();
        _pendingAnnotation = null;
        _penPoints = null;
        _notePressOnExisting = false;
        _notesBeforeDrag = null;
        _selectedNote = -1;
        _draggingNote = -1;
        _noteDragOriginal = null;
        _nextNumber = 1;
        _selectionUndo.Clear();
        _selectionRedo.Clear();
        CancelAngleDraft();
        RebuildNotePreview();
        UpdateToolButtons();
        _ocrText = null;

        OcrLayer.Children.Clear();
        OcrLayer.IsVisible = false;

        Selection.IsVisible = true;
        ApplyCreate(point);
        Cursor = CrossCursor;
    }

    private void ApplyCreate(Point current, bool constrain = false)
    {
        _toolbarManual = false;

        var x = Math.Min(_createStart.X, current.X);
        var y = Math.Min(_createStart.Y, current.Y);
        var width = Math.Abs(current.X - _createStart.X);
        var height = Math.Abs(current.Y - _createStart.Y);

        // Shift = 正方形（已锁比例时沿用比例）；否则只有锁定比例才约束
        var aspect = constrain
            ? (_aspectLocked && EffectiveAspect() > 0 ? EffectiveAspect() : 1)
            : (_aspectLocked ? EffectiveAspect() : 0);

        if (aspect > 0)
        {
            var size = constrain ? Math.Max(width, height) : width;
            width = Math.Max(size, MinSelectionSize);
            height = width / aspect;

            var growsLeft = current.X < _createStart.X;
            var growsUp = current.Y < _createStart.Y;
            x = growsLeft ? _createStart.X - width : _createStart.X;
            y = growsUp ? _createStart.Y - height : _createStart.Y;

            // 越界就按可用空间反算，保证比例不变
            if (x < 0)
            {
                width = Math.Max(MinSelectionSize, width + x);
                x = 0;
                height = width / aspect;
                if (growsUp) y = _createStart.Y - height;
            }

            if (y < 0)
            {
                height = Math.Max(MinSelectionSize, height + y);
                y = 0;
                width = height * aspect;
                if (growsLeft) x = _createStart.X - width;
            }

            if (x + width > Width)
            {
                width = Math.Max(MinSelectionSize, Width - x);
                height = width / aspect;
            }

            if (y + height > Height)
            {
                height = Math.Max(MinSelectionSize, Height - y);
                width = height * aspect;
                if (growsLeft) x = Math.Max(0, _createStart.X - width);
            }
        }

        _selection = new Rect(x, y, width, height);
        UpdateSelectionVisuals();
    }

    /// <summary>
    /// 比例锁下的拉边修正：拖哪个轴就驱动哪个轴，按比例推另一个轴。
    /// 边点保持对边和另一轴中线不动；角点钉住对角。
    /// </summary>
    private (double left, double top, double right, double bottom) ApplyAspectToResize(
        double left, double top, double right, double bottom)
    {
        var aspect = EffectiveAspect();
        if (!_aspectLocked || aspect <= 0)
            return (left, top, right, bottom);

        var w = right - left;
        var h = bottom - top;
        if (w <= 0 || h <= 0)
            return (left, top, right, bottom);

        var isCorner = _resizeHandle
            is ResizeHandle.NW or ResizeHandle.NE or ResizeHandle.SW or ResizeHandle.SE;
        var horizontal = _resizeHandle
            is ResizeHandle.E or ResizeHandle.W or ResizeHandle.NW or ResizeHandle.NE
            or ResizeHandle.SW or ResizeHandle.SE;
        var vertical = _resizeHandle
            is ResizeHandle.N or ResizeHandle.S or ResizeHandle.NW or ResizeHandle.NE
            or ResizeHandle.SW or ResizeHandle.SE;

        // 角点：哪个轴的相对变化大就用哪个驱动
        var driveWidth = horizontal && !vertical;
        if (isCorner)
        {
            var dw = Math.Abs(w - _selectionOrigin.Width) / Math.Max(1, _selectionOrigin.Width);
            var dh = Math.Abs(h - _selectionOrigin.Height) / Math.Max(1, _selectionOrigin.Height);
            driveWidth = dw >= dh;
        }

        var centerX = (left + right) / 2;
        var centerY = (top + bottom) / 2;

        if (driveWidth)
            h = w / aspect;
        else
            w = h * aspect;

        if (w < MinSelectionSize)
        {
            w = MinSelectionSize;
            h = w / aspect;
        }

        if (h < MinSelectionSize)
        {
            h = MinSelectionSize;
            w = h * aspect;
        }

        // 钉住不被拖的那一侧
        if (_resizeHandle is ResizeHandle.W or ResizeHandle.NW or ResizeHandle.SW)
            left = right - w;
        else if (_resizeHandle is ResizeHandle.E or ResizeHandle.NE or ResizeHandle.SE)
            right = left + w;
        else
        {
            left = centerX - w / 2;
            right = centerX + w / 2;
        }

        if (_resizeHandle is ResizeHandle.N or ResizeHandle.NW or ResizeHandle.NE)
            top = bottom - h;
        else if (_resizeHandle is ResizeHandle.S or ResizeHandle.SW or ResizeHandle.SE)
            bottom = top + h;
        else
        {
            top = centerY - h / 2;
            bottom = centerY + h / 2;
        }

        // 越界时整体平移回窗口内，保证比例不被破坏
        if (right > Width) { var d = right - Width; left -= d; right -= d; }
        if (bottom > Height) { var d = bottom - Height; top -= d; bottom -= d; }
        if (left < 0) { var d = -left; left += d; right += d; }
        if (top < 0) { var d = -top; top += d; bottom += d; }

        return (left, top, right, bottom);
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

        // 候选窗口也挖开遮罩：悬停时整窗预览更接近参考工具的「智能选区」
        _candidateDip = new Rect(x, y, width, height);
        UpdateDim();

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
        _candidateDip = default;
        UpdateDim();
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

        _uiProbe?.ReleaseTrees();
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

    /// <summary>方向键微调：选区和标注一起反向平移，保持屏幕位置，并进撤销栈。</summary>
    private void NudgeSelection(double dx, double dy)
    {
        if (!_hasSelection)
            return;

        var left = Math.Clamp(_selection.X + dx, 0, Math.Max(0, Width - _selection.Width));
        var top = Math.Clamp(_selection.Y + dy, 0, Math.Max(0, Height - _selection.Height));
        var moveX = left - _selection.X;
        var moveY = top - _selection.Y;

        if (Math.Abs(moveX) < 0.01 && Math.Abs(moveY) < 0.01)
            return;

        _selectionUndo.Push(new SelectionSnapshot(_selection, _notes.Items.ToList()));
        _selectionRedo.Clear();

        _selection = new Rect(left, top, _selection.Width, _selection.Height);
        _selectionOrigin = _selection;
        TranslateNotesCurrent(-moveX, -moveY);

        UpdateSelectionVisuals();
        UpdateHandles();
        UpdateSizeBadge(_selection);
        PositionActionBar(_selection);
        UpdateToolButtons();
    }

    /// <summary>直接平移当前标注（方向键微调用，不需要拖动快照）。</summary>
    private void TranslateNotesCurrent(double dx, double dy)
    {
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
            return;

        _notes.SetItems(_notes.Items.Select(note => TranslateNote(note, dx, dy)).ToList());
        RebuildNotePreview();
    }

    private void ApplyMove(Point current)
    {
        // 选区一动就恢复「工具条跟随」，对齐参考的 resetPositionForSelection 语义
        _toolbarManual = false;

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
        TranslateNotesForSelectionChange(left - _selectionOrigin.X, top - _selectionOrigin.Y);
    }

    private void ApplyResize(Point current)
    {
        _toolbarManual = false;

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

        // 比例锁：拖完之后按比例修正另一轴（拖哪条边就驱动哪个轴）
        (left, top, right, bottom) = ApplyAspectToResize(left, top, right, bottom);

        _selection = new Rect(left, top, right - left, bottom - top);
        UpdateSelectionVisuals();
        TranslateNotesForSelectionChange(left - _selectionOrigin.X, top - _selectionOrigin.Y);
    }

    /// <summary>
    /// 选区原点变化时，把标注反向平移，保持它们在屏幕上的位置不变。
    /// 从拖动开始时的快照重算，避免逐帧累积误差。
    /// </summary>
    private void TranslateNotesForSelectionChange(double deltaX, double deltaY)
    {
        if (_notesBeforeDrag is null
            || (Math.Abs(deltaX) < 0.01 && Math.Abs(deltaY) < 0.01))
            return;

        _notes.SetItems(_notesBeforeDrag.Select(
            note => TranslateNote(note, -deltaX, -deltaY)));
        RebuildNotePreview();
    }

    private void UpdateSelectionVisuals()
    {
        var visible = _selection.Width > 0 && _selection.Height > 0;
        var radius = SelectionRadius();

        Selection.IsVisible = visible;
        SelectionHalo.IsVisible = visible;
        Selection.RadiusX = radius;
        Selection.RadiusY = radius;
        SelectionHalo.RadiusX = radius > 0 ? radius + 1 : 0;
        SelectionHalo.RadiusY = radius > 0 ? radius + 1 : 0;

        Canvas.SetLeft(Selection, _selection.X);
        Canvas.SetTop(Selection, _selection.Y);
        Selection.Width = _selection.Width;
        Selection.Height = _selection.Height;

        // 外圈 1px 深色描边：亮背景上也能把选区边界和内容分开
        Canvas.SetLeft(SelectionHalo, _selection.X - 1);
        Canvas.SetTop(SelectionHalo, _selection.Y - 1);
        SelectionHalo.Width = _selection.Width + 2;
        SelectionHalo.Height = _selection.Height + 2;

        UpdateSizeBadge(_selection);
        UpdateDim();

        if (_hasSelection)
        {
            UpdateHandles();
            PositionActionBar(_selection);
        }
    }

    /// <summary>
    /// 遮罩只压暗选区之外：全屏矩形减去选区（或悬停候选）挖洞。
    /// 选区保持原图亮度，这是选区和参考工具最直观的差别。
    /// </summary>
    private void UpdateDim()
    {
        var bounds = new Rect(0, 0, Math.Max(Width, 1), Math.Max(Height, 1));

        Rect? hole = null;

        if (_selection.Width > 0 && _selection.Height > 0)
            hole = _selection;
        else if (CandidateBox.IsVisible && _candidateDip.Width > 0 && _candidateDip.Height > 0)
            hole = _candidateDip;

        if (hole is not { Width: > 0, Height: > 0 })
        {
            DimLayer.Data = new RectangleGeometry(bounds);
            return;
        }

        DimLayer.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(bounds),
            new RectangleGeometry(hole.Value)
            {
                RadiusX = hole is { } h && h == _selection ? SelectionRadius() : 0,
                RadiusY = hole is { } r && r == _selection ? SelectionRadius() : 0
            });
    }

    /// <summary>选区预览半径：不超过短边的一半。</summary>
    private double SelectionRadius() =>
        _cornerRadius <= 0
            ? 0
            : Math.Min(_cornerRadius, Math.Min(_selection.Width, _selection.Height) / 2);

    /// <summary>光标十字线：还没框选时帮助对齐（参考 snow-shot 的实时参考线）。</summary>
    private void UpdateCrosshair(Point point)
    {
        var show = !_hasSelection
            && !OcrLayer.IsVisible
            && !IsPointInActionBar(point)
            && !_isActing;

        CrosshairLayer.IsVisible = show;

        if (!show)
            return;

        CrosshairV.StartPoint = new Point(point.X, 0);
        CrosshairV.EndPoint = new Point(point.X, Math.Max(Height, 1));
        CrosshairH.StartPoint = new Point(0, point.Y);
        CrosshairH.EndPoint = new Point(Math.Max(Width, 1), point.Y);
    }

    /// <summary>
    /// 拖动/拉伸时在光标旁显示 4 倍放大镜。
    /// 直接用冻结帧按比例缩放，不做像素重采样，没有额外内存。
    /// </summary>
    private void UpdateMagnifier(Point point)
    {
        var show = _frozenBitmap is not null
            && _mode != DragMode.None
            && _pendingAnnotation is null
            && !IsPointInActionBar(point);

        if (!show)
        {
            Magnifier.IsVisible = false;
            return;
        }

        Magnifier.IsVisible = true;
        MagnifierImage.Source = _frozenBitmap;
        MagnifierImage.Width = Math.Max(Width, 1);
        MagnifierImage.Height = Math.Max(Height, 1);

        if (MagnifierImage.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = MagnifierZoom;
            scale.ScaleY = MagnifierZoom;
        }

        // 让光标位置正好落在放大镜中心
        Canvas.SetLeft(MagnifierImage, -(point.X * MagnifierZoom) + MagnifierSize / 2);
        Canvas.SetTop(MagnifierImage, -(point.Y * MagnifierZoom) + MagnifierSize / 2);

        // 依次尝试右下 / 左下 / 右上 / 左上，避开工具条和窗口边缘
        var toolbarLeft = Canvas.GetLeft(ActionBar);
        var toolbarTop = Canvas.GetTop(ActionBar);
        var hasToolbar = ActionBar.IsVisible
            && !double.IsNaN(toolbarLeft)
            && !double.IsNaN(toolbarTop)
            && ActionBar.Bounds.Width > 0;

        var toolbar = hasToolbar
            ? new Rect(
                toolbarLeft,
                toolbarTop,
                ActionBar.Bounds.Width,
                ActionBar.Bounds.Height).Inflate(8)
            : default;

        var placed = PlaceMagnifier(
            point,
            MagnifierSize,
            new Size(Width, Height),
            hasToolbar ? toolbar : null);

        Canvas.SetLeft(Magnifier, placed.X);
        Canvas.SetTop(Magnifier, placed.Y);
    }

    /// <summary>
    /// 放大镜位置：优先右下，贴边或压到工具条就换到另一侧；都放不下就钳制在窗口内。
    /// 抽成纯函数，方便验证「不压工具条」这条规则。
    /// </summary>
    internal static Rect PlaceMagnifier(Point cursor, double size, Size bounds, Rect? avoid, double gap = 28)
    {
        var offsets = new[]
        {
            new Point(gap, gap),
            new Point(-size - gap, gap),
            new Point(gap, -size - gap),
            new Point(-size - gap, -size - gap)
        };

        foreach (var offset in offsets)
        {
            var rect = new Rect(cursor.X + offset.X, cursor.Y + offset.Y, size, size);

            if (rect.X < 8 || rect.Y < 8 || rect.Right > bounds.Width - 8 || rect.Bottom > bounds.Height - 8)
                continue;

            if (avoid is { Width: > 0 } && rect.Intersects(avoid.Value))
                continue;

            return rect;
        }

        var left = Math.Clamp(
            cursor.X + gap,
            8,
            Math.Max(8, bounds.Width - size - 8));
        var top = Math.Clamp(
            cursor.Y + gap,
            8,
            Math.Max(8, bounds.Height - size - 8));

        return new Rect(left, top, size, size);
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
        // 悬停在工具条 / 尺寸浮条 / 弹出层上：始终显示普通箭头
        if (IsPointInActionBar(point)
            || IsPointInControl(SizeBadge, point)
            || IsPointInControl(CustomColorPanel, point)
            || IsPointInControl(ToolGroupPopover, point)
            || IsPointInControl(ResultCard, point))
        {
            Cursor = ArrowCursor;
            return;
        }

        if (!_hasSelection || OcrLayer.IsVisible)
        {
            Cursor = CrossCursor;
            return;
        }

        // 选中标注的旋转/缩放点优先于选区移动
        if (_tool == AnnotationTool.None)
        {
            if (HitNoteRotateHandle(point))
            {
                Cursor = HandCursor;
                return;
            }

            var noteHandle = HitNoteHandle(point);
            if (noteHandle != ResizeHandle.None)
            {
                Cursor = ResizeCursorFor(noteHandle);
                return;
            }
        }

        // 边框优先：即使正在用绘图工具，按在控制点上也是拉伸
        var handle = HitHandle(point);
        if (handle != ResizeHandle.None)
        {
            Cursor = ResizeCursorFor(handle);
            return;
        }

        // 绘图工具：选区内显示工具光标，不再伪装成「移动选区」
        if (_tool != AnnotationTool.None)
        {
            if (!_selection.Contains(point))
            {
                Cursor = CrossCursor;
                return;
            }

            Cursor = _tool == AnnotationTool.Text
                ? IbeamCursor
                : CrossCursor;
            return;
        }

        Cursor = _selection.Contains(point) ? MoveCursor : CrossCursor;
    }

    private static Cursor ResizeCursorFor(ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW or ResizeHandle.SE => NwseCursor,
        ResizeHandle.NE or ResizeHandle.SW => NeswCursor,
        ResizeHandle.N or ResizeHandle.S => NorthSouthCursor,
        ResizeHandle.E or ResizeHandle.W => WestEastCursor,
        _ => ArrowCursor
    };

    private bool IsPointInControl(Control control, Point point)
    {
        if (!control.IsVisible)
            return false;

        // 刚显示、还没跑过布局时 Bounds 是空的，用 DesiredSize 兜底
        var size = control.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            size = control.DesiredSize;

        return control.TranslatePoint(new Point(0, 0), this) is { } origin
               && new Rect(origin, size).Contains(point);
    }

    /// <summary>
    /// Esc / 右键用于取消“进行中的操作”：拖选区、拖标注、画一半、橡皮擦、拖工具条。
    /// 返回 true 表示这次按键/右键已经被取消动作消费。
    /// </summary>
    private bool CancelActiveDrag()
    {
        var active = _pendingAnnotation is not null
                     || _mode != DragMode.None
                     || _erasing
                     || _draggingToolbar;

        if (!active)
            return false;

        _draggingToolbar = false;
        _erasing = false;

        if (_mode == DragMode.Create)
        {
            _mode = DragMode.None;
            _pendingAnnotation = null;
            _penPoints = null;
            ResetSelection();
            return true;
        }

        if (_mode is DragMode.Move or DragMode.Resize)
        {
            _selection = _selectionOrigin;
            if (_notesBeforeDrag is { } notes)
                _notes.SetItems(notes);
        }
        else if (_mode == DragMode.NoteEdit
                 && _draggingNote >= 0
                 && _noteDragOriginal is not null)
        {
            _notes.Replace(_draggingNote, _noteDragOriginal);
        }
        else if (_mode == DragMode.NoteResize
                 && _noteResizeIndex >= 0
                 && _noteResizeOriginal is { } resizeOriginal)
        {
            _notes.Replace(_noteResizeIndex, resizeOriginal);
        }
        else if (_mode == DragMode.NoteRotate
                 && _noteRotateIndex >= 0
                 && _noteRotateOriginal is { } rotateOriginal)
        {
            _notes.Replace(_noteRotateIndex, rotateOriginal);
        }

        _mode = DragMode.None;
        _draggingNote = -1;
        _noteDragOriginal = null;
        _notesBeforeDrag = null;
        _pendingAnnotation = null;
        _penPoints = null;
        _noteDragMoved = false;
        _noteResizeIndex = -1;
        _noteResizeOriginal = null;
        _noteResizeMoved = false;
        _noteRotateIndex = -1;
        _noteRotateOriginal = null;
        _noteRotateMoved = false;
        HideTextEditor();
        Magnifier.IsVisible = false;
        ClearGuides();

        if (_hasSelection)
        {
            UpdateSelectionVisuals();
            UpdateHandles();
            UpdateSizeBadge(_selection);
            PositionActionBar(_selection);
        }

        RebuildNotePreview();
        UpdateToolButtons();
        return true;
    }

    /// <summary>
    /// 右键逐级后退，和参考工具的肌肉记忆一致：
    /// 拾色器 / 标注工具 → 指针；有选区 → 清空重选；什么都没有 → 取消截图。
    /// </summary>
    private void HandleRightClick()
    {
        // 角度草稿：先清草稿，不退出工具（再按一次右键才退）
        if (_angleDraft.IsActive || _anglePhase != AnglePhase.None)
        {
            CancelAngleDraft();
            return;
        }

        // 取色是最外层：先退出取色，再谈别的
        if (_colorSampling)
        {
            ExitColorSampling();
            return;
        }

        if (CustomColorPanel.IsVisible)
        {
            CustomColorPanel.IsVisible = false;
            return;
        }

        // 正在拖选区/拖标注/画一半：右键先取消这次操作
        if (CancelActiveDrag())
            return;

        if (_tool != AnnotationTool.None)
        {
            _tool = AnnotationTool.None;
            UpdateToolButtons();
            return;
        }

        if (OcrLayer.IsVisible)
        {
            ClearOcrOverlay();
            return;
        }

        if (_hasSelection)
        {
            ResetSelection();
            return;
        }

        Action = CaptureAction.None;
        Close();
    }

    private void ResetSelection()
    {
        _hasSelection = false;
        _selectedNote = -1;
        _draggingNote = -1;
        _noteDragOriginal = null;
        _selection = default;
        Selection.IsVisible = false;
        SelectionHalo.IsVisible = false;
        Magnifier.IsVisible = false;
        HideCandidate();
        HandleLayer.IsVisible = false;
        ActionBar.IsVisible = false;
        SizeBadge.IsVisible = false;
        HideToolGroupPopover();
        HintBar.IsVisible = true;
        HideResultCard();
        _notes.Clear();
        _nextNumber = 1;
        _pendingAnnotation = null;
        _penPoints = null;
        _notesBeforeDrag = null;
        _selectionUndo.Clear();
        _selectionRedo.Clear();
        RebuildNotePreview();
        UpdateToolButtons();
        UpdateDim();
        Cursor = CrossCursor;
    }

    // ---------- 标注 ----------

    /// <summary>指针工具：退出标注模式，回到移动/拉伸选区。</summary>
    private void OnSelectToolClick(object? sender, RoutedEventArgs e)
    {
        _tool = AnnotationTool.None;
        UpdateToolButtons();
    }

    private void OnRectToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Rectangle);
    private void OnEllipseToolClick(object? sender, RoutedEventArgs e) => SetTool(AnnotationTool.Ellipse);

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
            _noteColor = Color.Parse(hex);
            UpdateStyleButtons();
        }
    }

    // ---------- 自定义颜色 ----------

    private readonly List<Color> _recentColors = [];
    private double _pickerHue;
    private double _pickerSaturation = 1;
    private double _pickerValue = 1;
    private Color _pickerColor = NoteColor;

    private void OnCustomColorClick(object? sender, RoutedEventArgs e)
    {
        if (CustomColorPanel.IsVisible)
        {
            CustomColorPanel.IsVisible = false;
            return;
        }

        ShowColorPanel();
    }

    private void ShowColorPanel()
    {
        _pickerColor = _noteColor;
        (_pickerHue, _pickerSaturation, _pickerValue) = RgbToHsv(_noteColor);
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
        HexText.Text = $"#{_pickerColor.R:X2}{_pickerColor.G:X2}{_pickerColor.B:X2}";

        var width = SvArea.Bounds.Width > 1 ? SvArea.Bounds.Width : 192;
        var height = SvArea.Bounds.Height > 1 ? SvArea.Bounds.Height : 110;

        SvCursor.Margin = new Thickness(
            _pickerSaturation * width - 6,
            (1 - _pickerValue) * height - 6,
            0,
            0);
    }

    private void OnApplyCustomColor(object? sender, RoutedEventArgs e)
    {
        _noteColor = _pickerColor;
        PushRecentColor(_pickerColor);
        UpdateStyleButtons();
        CustomColorPanel.IsVisible = false;
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

    private void OnWidth2Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(2);
    private void OnWidth3Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(3);
    private void OnWidth4Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(4);
    private void OnWidth8Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(8);
    private void OnWidth12Menu(object? sender, RoutedEventArgs e) => SetNoteWidth(12);

    private void SetNoteWidth(double width)
    {
        _noteWidth = width;
        UpdateStyleButtons();
    }

    private void OnDashSolidMenu(object? sender, RoutedEventArgs e) => SetNoteDash(LineDash.Solid);
    private void OnDashDashMenu(object? sender, RoutedEventArgs e) => SetNoteDash(LineDash.Dash);
    private void OnDashDotMenu(object? sender, RoutedEventArgs e) => SetNoteDash(LineDash.Dot);

    private void SetNoteDash(LineDash dash)
    {
        _noteDash = dash;
        UpdateStyleButtons();
    }

    private void OnFillNoneMenu(object? sender, RoutedEventArgs e) => SetNoteFill(ShapeFill.None);
    private void OnFillSoftMenu(object? sender, RoutedEventArgs e) => SetNoteFill(ShapeFill.Soft);
    private void OnFillSolidMenu(object? sender, RoutedEventArgs e) => SetNoteFill(ShapeFill.Solid);

    private void SetNoteFill(ShapeFill fill)
    {
        if (_tool == AnnotationTool.Bubble)
            _bubbleFill = fill;
        else
            _noteFill = fill;

        UpdateStyleButtons();
    }

    private double ActiveFontSize => _tool switch
    {
        AnnotationTool.Watermark => _watermarkFontSize,
        AnnotationTool.Bubble => _bubbleFontSize,
        _ => _noteFontSize
    };

    private void OnOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var value = Math.Clamp(e.NewValue / 100, 0.2, 1);

        switch (_tool)
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

        OpacityValue.Text = $"{e.NewValue:0}%";
    }

    private void OnRadiusChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _noteRadius = Math.Max(0, e.NewValue);
        RadiusValue.Text = $"{e.NewValue:0} px";
    }

    private void OnFont14Menu(object? sender, RoutedEventArgs e) => SetFontSize(14);
    private void OnFont16Menu(object? sender, RoutedEventArgs e) => SetFontSize(16);
    private void OnFont20Menu(object? sender, RoutedEventArgs e) => SetFontSize(20);
    private void OnFont24Menu(object? sender, RoutedEventArgs e) => SetFontSize(24);
    private void OnFont32Menu(object? sender, RoutedEventArgs e) => SetFontSize(32);

    private void SetFontSize(double size)
    {
        switch (_tool)
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

        UpdateStyleButtons();
    }

    private void OnBoldClick(object? sender, RoutedEventArgs e)
    {
        _noteBold = !_noteBold;
        UpdateStyleButtons();
    }

    private void OnNumber16Menu(object? sender, RoutedEventArgs e) => SetNumberSize(16);
    private void OnNumber24Menu(object? sender, RoutedEventArgs e) => SetNumberSize(24);
    private void OnNumber32Menu(object? sender, RoutedEventArgs e) => SetNumberSize(32);
    private void OnNumber48Menu(object? sender, RoutedEventArgs e) => SetNumberSize(48);

    private void SetNumberSize(double size)
    {
        _numberSize = size;
        UpdateStyleButtons();
    }

    private void OnMosaicPixelMenu(object? sender, RoutedEventArgs e) => ToggleMosaicBlur(false);
    private void OnMosaicBlurMenu(object? sender, RoutedEventArgs e) => ToggleMosaicBlur(true);

    /// <summary>切换马赛克像素 / 模糊；菜单和测试共用。</summary>
    internal void ToggleMosaicBlur(bool blur)
    {
        _mosaicBlur = blur;
        UpdateStyleButtons();
    }

    private void OnBrush8Menu(object? sender, RoutedEventArgs e) => SetMosaicCell(8);
    private void OnBrush12Menu(object? sender, RoutedEventArgs e) => SetMosaicCell(12);
    private void OnBrush20Menu(object? sender, RoutedEventArgs e) => SetMosaicCell(20);

    private void SetMosaicCell(int cell)
    {
        _mosaicCell = cell;
        UpdateStyleButtons();
    }

    private void OnCornerRadiusClick(object? sender, RoutedEventArgs e)
    {
        _cornerRadius = _cornerRadius > 0 ? 0 : 10;
        UpdateStyleButtons();

        if (_hasSelection)
            UpdateSelectionVisuals();
    }

    private void UpdateStyleButtons()
    {
        SetSelected(ColorAmberButton, IsColor("#E5A44C"));
        SetSelected(ColorWhiteButton, IsColor("#FFFFFF"));
        SetSelected(ColorRedButton, IsColor("#FF5F5F"));
        SetSelected(ColorGreenButton, IsColor("#5FD08C"));
        SetSelected(ColorBlueButton, IsColor("#5FA8FF"));

        WidthMenuButton.Content = $"{_noteWidth:0} px";
        SetSelected(DashSolidButton, _noteDash == LineDash.Solid);
        SetSelected(DashDashButton, _noteDash == LineDash.Dash);
        SetSelected(DashDotButton, _noteDash == LineDash.Dot);
        var fill = _tool == AnnotationTool.Bubble ? _bubbleFill : _noteFill;
        SetSelected(FillNoneButton, fill == ShapeFill.None);
        SetSelected(FillSoftButton, fill == ShapeFill.Soft);
        SetSelected(FillSolidButton, fill == ShapeFill.Solid);

        var opacity = _tool switch
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
        NumberSizeMenuButton.Content = $"{_numberSize:0} px";

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
        RebuildNotePreview();
    }

    /// <summary>指针下面最上面的一条标注；没有返回 -1。</summary>
    private int HitNote(Point point)
    {
        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);

        for (var index = _notes.Items.Count - 1; index >= 0; index--)
        {
            if (HitTestNote(_notes.Items[index], local))
                return index;
        }

        return -1;
    }

    /// <summary>橡皮擦：擦掉指针下最上面的一条标注。</summary>
    private void EraseAt(Point point)
    {
        var hit = HitNote(point);
        if (hit < 0)
            return;

        _notes.RemoveAt(hit);
        MarkNoteMutation();

        if (_selectedNote == hit)
            _selectedNote = -1;
        else if (_selectedNote > hit)
            _selectedNote--;

        RebuildNotePreview();
        UpdateToolButtons();
    }

    /// <summary>开始拖动选中标注的缩放控制点。</summary>
    private void BeginNoteResize(Point point, ResizeHandle handle)
    {
        _mode = DragMode.NoteResize;
        _noteResizeHandle = handle;
        _noteResizeIndex = _selectedNote;
        _noteResizeOriginal = _notes.Items[_selectedNote];
        _noteResizeBounds = NoteBounds(_noteResizeOriginal);
        _noteResizeMoved = false;
        HideTextEditor();
    }

    /// <summary>按控制点缩放标注；文字不显示控制点，其余类型按包围盒等比映射。</summary>
    private void ApplyNoteResize(Point point)
    {
        if (_noteResizeOriginal is null
            || _noteResizeIndex < 0
            || _noteResizeIndex >= _notes.Items.Count)
            return;

        var current = new Point(point.X - _selection.X, point.Y - _selection.Y);
        var rect = _noteResizeBounds;
        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        if (_noteResizeHandle is ResizeHandle.NW or ResizeHandle.W or ResizeHandle.SW)
            left = Math.Min(current.X, right - MinNoteSize);
        if (_noteResizeHandle is ResizeHandle.NE or ResizeHandle.E or ResizeHandle.SE)
            right = Math.Max(current.X, left + MinNoteSize);
        if (_noteResizeHandle is ResizeHandle.NW or ResizeHandle.N or ResizeHandle.NE)
            top = Math.Min(current.Y, bottom - MinNoteSize);
        if (_noteResizeHandle is ResizeHandle.SW or ResizeHandle.S or ResizeHandle.SE)
            bottom = Math.Max(current.Y, top + MinNoteSize);

        left = Math.Clamp(left, 0, Math.Max(0, _selection.Width - MinNoteSize));
        top = Math.Clamp(top, 0, Math.Max(0, _selection.Height - MinNoteSize));
        right = Math.Clamp(right, left + MinNoteSize, Math.Max(MinNoteSize, _selection.Width));
        bottom = Math.Clamp(bottom, top + MinNoteSize, Math.Max(MinNoteSize, _selection.Height));

        var target = new Rect(left, top, right - left, bottom - top);
        if (target.Width < MinNoteSize || target.Height < MinNoteSize)
            return;

        if (!_noteResizeMoved)
        {
            _noteResizeMoved = true;
            MarkNoteMutation();
        }

        var scaled = AnnotationGeometry.Scale(_noteResizeOriginal, _noteResizeBounds, target);
        if (scaled is DistanceAnnotation distance)
            scaled = distance with { Label = FormatDistance(distance.From, distance.To) };

        _notes.Replace(_noteResizeIndex, scaled);
        RebuildNotePreview();
    }

    /// <summary>开始拖动旋转手柄。</summary>
    private void BeginNoteRotate(Point point)
    {
        _mode = DragMode.NoteRotate;
        _noteRotateIndex = _selectedNote;
        _noteRotateOriginal = _notes.Items[_selectedNote];
        _noteRotateCenter = NoteRotationCenter(_noteRotateOriginal);
        _noteRotateBase = AnnotationGeometry.RotationOf(_noteRotateOriginal);
        _noteRotateStartVector = new Point(
            point.X - _selection.X - _noteRotateCenter.X,
            point.Y - _selection.Y - _noteRotateCenter.Y);
        _noteRotateMoved = false;
        HideTextEditor();
    }

    /// <summary>按鼠标相对中心的角度设置旋转；按住 Shift 吸附 15°。</summary>
    private void ApplyNoteRotate(Point point, bool snap)
    {
        if (_noteRotateOriginal is null
            || _noteRotateIndex < 0
            || _noteRotateIndex >= _notes.Items.Count)
            return;

        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);
        var vector = new Point(
            local.X - _noteRotateCenter.X,
            local.Y - _noteRotateCenter.Y);

        if (Math.Abs(vector.X) < 0.001 && Math.Abs(vector.Y) < 0.001)
            return;

        var startAngle = Math.Atan2(_noteRotateStartVector.Y, _noteRotateStartVector.X);
        var currentAngle = Math.Atan2(vector.Y, vector.X);
        var angle = _noteRotateBase + (currentAngle - startAngle) * 180 / Math.PI;

        if (snap)
            angle = Math.Round(angle / 15) * 15;

        angle = NormalizeAngle(angle);

        if (!_noteRotateMoved)
        {
            _noteRotateMoved = true;
            MarkNoteMutation();
        }

        _notes.Replace(
            _noteRotateIndex,
            AnnotationGeometry.RotateTo(_noteRotateOriginal, angle));
        RebuildNotePreview();
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle < -180) angle += 360;
        return angle;
    }

    private void ApplyNoteDrag(Point point)    {        if (_draggingNote < 0 || _noteDragOriginal is null || _draggingNote >= _notes.Items.Count)
            return;

        // 鼠标点换算成选区相对坐标，位移直接加在原始图元上
        var dx = point.X - _selection.X - _noteDragStart.X;
        var dy = point.Y - _selection.Y - _noteDragStart.Y;

        if (!_noteDragMoved && (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01))
        {
            _noteDragMoved = true;
            MarkNoteMutation();
        }

        _notes.Replace(_draggingNote, TranslateNote(_noteDragOriginal, dx, dy));
        RebuildNotePreview();
    }

    /// <summary>删除选中的标注（Delete / Backspace）。</summary>
    internal void DeleteSelectedNote()
    {
        if (_selectedNote < 0 || _selectedNote >= _notes.Items.Count)
            return;

        _notes.RemoveAt(_selectedNote);
        _selectedNote = -1;
        MarkNoteMutation();
        RebuildNotePreview();
        UpdateToolButtons();
    }

    private static IAnnotation TranslateNote(IAnnotation item, double dx, double dy) => item switch
    {
        ShapeAnnotation shape => shape with
        {
            Rect = new Rect(shape.Rect.X + dx, shape.Rect.Y + dy, shape.Rect.Width, shape.Rect.Height)
        },
        ArrowAnnotation arrow => arrow with
        {
            From = new Point(arrow.From.X + dx, arrow.From.Y + dy),
            To = new Point(arrow.To.X + dx, arrow.To.Y + dy)
        },
        LineAnnotation line => line with
        {
            From = new Point(line.From.X + dx, line.From.Y + dy),
            To = new Point(line.To.X + dx, line.To.Y + dy)
        },
        DistanceAnnotation distance => distance with
        {
            From = new Point(distance.From.X + dx, distance.From.Y + dy),
            To = new Point(distance.To.X + dx, distance.To.Y + dy)
        },
        AngleAnnotation angle => angle with
        {
            Vertex = new Point(angle.Vertex.X + dx, angle.Vertex.Y + dy),
            A = new Point(angle.A.X + dx, angle.A.Y + dy),
            B = new Point(angle.B.X + dx, angle.B.Y + dy)
        },
        BracketAnnotation bracket => bracket with
        {
            Rect = new Rect(
                bracket.Rect.X + dx, bracket.Rect.Y + dy,
                bracket.Rect.Width, bracket.Rect.Height)
        },
        SpeechBubbleAnnotation bubble => bubble with
        {
            Rect = new Rect(
                bubble.Rect.X + dx, bubble.Rect.Y + dy,
                bubble.Rect.Width, bubble.Rect.Height),
            Tail = new Point(bubble.Tail.X + dx, bubble.Tail.Y + dy)
        },
        PenAnnotation pen => pen with
        {
            Points = pen.Points.Select(point => new Point(point.X + dx, point.Y + dy)).ToList()
        },
        MosaicAnnotation mosaic => mosaic with
        {
            Rect = new Rect(mosaic.Rect.X + dx, mosaic.Rect.Y + dy, mosaic.Rect.Width, mosaic.Rect.Height)
        },
        SpotlightAnnotation spotlight => spotlight with
        {
            Rect = new Rect(
                spotlight.Rect.X + dx, spotlight.Rect.Y + dy,
                spotlight.Rect.Width, spotlight.Rect.Height)
        },
        MagnifierAnnotation magnifier => magnifier with
        {
            Rect = new Rect(
                magnifier.Rect.X + dx, magnifier.Rect.Y + dy,
                magnifier.Rect.Width, magnifier.Rect.Height)
        },
        WatermarkAnnotation watermark => watermark with
        {
            Area = new Rect(
                watermark.Area.X + dx, watermark.Area.Y + dy,
                watermark.Area.Width, watermark.Area.Height)
        },
        TextAnnotation text => text with
        {
            Origin = new Point(text.Origin.X + dx, text.Origin.Y + dy)
        },
        NumberAnnotation number => number with
        {
            Center = new Point(number.Center.X + dx, number.Center.Y + dy)
        },
        _ => item
    };

    /// <summary>命中测试：空心形状要求靠近边框，实心形状整块可点。</summary>
    private static bool HitTestNote(IAnnotation item, Point point)
    {
        const double slop = 6;

        var rotation = AnnotationGeometry.RotationOf(item);
        if (Math.Abs(rotation) > 0.01)
            point = RotatePoint(point, NoteRotationCenter(item), -rotation);

        switch (item)
        {
            case ShapeAnnotation shape:
                var outer = Grow(shape.Rect, shape.Width / 2 + slop);

                if (shape.Fill != ShapeFill.None)
                    return outer.Contains(point);

                // 空心：中间留空，点进去是移动选区 / 画新东西
                var inner = Grow(shape.Rect, -(shape.Width / 2 + slop));
                return outer.Contains(point)
                    && (inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(point));

            case ArrowAnnotation arrow:
                return DistanceToSegment(point, arrow.From, arrow.To) <= arrow.Width / 2 + slop;

            case LineAnnotation line:
                return DistanceToSegment(point, line.From, line.To) <= line.Width / 2 + slop;

            case DistanceAnnotation distance:
                return DistanceToSegment(point, distance.From, distance.To)
                       <= distance.Width / 2 + slop;

            case AngleAnnotation angle:
                return DistanceToSegment(point, angle.Vertex, angle.A) <= angle.Width / 2 + slop
                       || DistanceToSegment(point, angle.Vertex, angle.B) <= angle.Width / 2 + slop;

            case BracketAnnotation bracket:
                var bracketPoints = AnnotationGeometry.BracketPoints(bracket);
                for (var index = 1; index < bracketPoints.Length; index++)
                {
                    if (DistanceToSegment(point, bracketPoints[index - 1], bracketPoints[index])
                        <= bracket.Width / 2 + slop)
                        return true;
                }

                return false;

            case SpeechBubbleAnnotation bubble:
            {
                if (bubble.Rect.Contains(point))
                    return true;

                var (first, second) = AnnotationGeometry.BubbleTailBase(bubble.Rect, bubble.Tail);
                return DistanceToSegment(point, first, bubble.Tail) <= bubble.Width / 2 + slop
                       || DistanceToSegment(point, bubble.Tail, second) <= bubble.Width / 2 + slop;
            }

            case PenAnnotation pen:
                for (var index = 1; index < pen.Points.Count; index++)
                {
                    if (DistanceToSegment(point, pen.Points[index - 1], pen.Points[index])
                        <= pen.Width / 2 + slop)
                        return true;
                }

                return false;

            case MosaicAnnotation mosaic:
                return mosaic.Rect.Contains(point);

            case SpotlightAnnotation spotlight:
                return spotlight.Rect.Contains(point);

            case MagnifierAnnotation magnifier:
                return NoteBounds(magnifier).Contains(point);

            case WatermarkAnnotation watermark:
                return WatermarkLayout.Hit(watermark, point);

            case TextAnnotation text:
                return NoteBounds(text).Contains(point);

            case NumberAnnotation number:
                return Distance(point, number.Center) <= number.Size / 2 + slop;

            default:
                return false;
        }
    }

    private static readonly ResizeHandle[] NoteResizeHandles =
    [
        ResizeHandle.NW, ResizeHandle.N, ResizeHandle.NE, ResizeHandle.E,
        ResizeHandle.SE, ResizeHandle.S, ResizeHandle.SW, ResizeHandle.W
    ];

    private static Point NoteHandlePoint(Rect bounds, ResizeHandle handle) => handle switch
    {
        ResizeHandle.NW => new Point(bounds.Left, bounds.Top),
        ResizeHandle.N => new Point(bounds.Center.X, bounds.Top),
        ResizeHandle.NE => new Point(bounds.Right, bounds.Top),
        ResizeHandle.E => new Point(bounds.Right, bounds.Center.Y),
        ResizeHandle.SE => new Point(bounds.Right, bounds.Bottom),
        ResizeHandle.S => new Point(bounds.Center.X, bounds.Bottom),
        ResizeHandle.SW => new Point(bounds.Left, bounds.Bottom),
        ResizeHandle.W => new Point(bounds.Left, bounds.Center.Y),
        _ => new Point(bounds.Center.X, bounds.Center.Y)
    };

    private static Point NoteRotateHandlePoint(Rect bounds)
    {
        var y = bounds.Top - RotateHandleDistance;
        if (y < 0)
            y = bounds.Bottom + RotateHandleDistance;

        return new Point(bounds.Center.X, y);
    }

    /// <summary>命中旋转手柄（局部坐标）。</summary>
    private bool HitNoteRotateHandle(Point point)
    {
        if (_tool != AnnotationTool.None
            || _selectedNote < 0
            || _selectedNote >= _notes.Items.Count)
            return false;

        var note = _notes.Items[_selectedNote];
        if (!AnnotationGeometry.IsRotatable(note))
            return false;

        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);
        return Distance(local, NoteRotateHandlePoint(NoteBounds(note))) <= 7;
    }

    /// <summary>命中选中标注的缩放控制点（局部坐标）。</summary>
    private ResizeHandle HitNoteHandle(Point point)
    {
        if (_tool != AnnotationTool.None
            || _selectedNote < 0
            || _selectedNote >= _notes.Items.Count)
            return ResizeHandle.None;

        var note = _notes.Items[_selectedNote];
        if (!AnnotationGeometry.IsScalable(note))
            return ResizeHandle.None;

        var local = new Point(point.X - _selection.X, point.Y - _selection.Y);
        var bounds = NoteBounds(note);

        foreach (var handle in NoteResizeHandles)
        {
            if (Distance(local, NoteHandlePoint(bounds, handle)) <= 7)
                return handle;
        }

        return ResizeHandle.None;
    }

    /// <summary>选中标注的 8 个缩放点；文字不显示。</summary>
    private void UpdateNoteHandles()
    {
        NoteHandleLayer.Children.Clear();
        NoteHandleLayer.IsVisible = false;

        if (_selectedNote < 0 || _selectedNote >= _notes.Items.Count)
            return;

        var note = _notes.Items[_selectedNote];
        if (!AnnotationGeometry.IsScalable(note))
            return;

        NoteHandleLayer.Width = _selection.Width;
        NoteHandleLayer.Height = _selection.Height;
        Canvas.SetLeft(NoteHandleLayer, _selection.X);
        Canvas.SetTop(NoteHandleLayer, _selection.Y);

        var bounds = NoteBounds(note);
        foreach (var handle in NoteResizeHandles)
        {
            var point = NoteHandlePoint(bounds, handle);
            var dot = new Rectangle
            {
                Width = 7,
                Height = 7,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.Parse("#FFE5A44C")),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(dot, point.X - 3.5);
            Canvas.SetTop(dot, point.Y - 3.5);
            NoteHandleLayer.Children.Add(dot);
        }

        if (AnnotationGeometry.IsRotatable(note))
        {
            var rotate = NoteRotateHandlePoint(bounds);
            var anchor = rotate.Y < bounds.Top
                ? new Point(bounds.Center.X, bounds.Top)
                : new Point(bounds.Center.X, bounds.Bottom);
            var stem = new Line
            {
                StartPoint = anchor,
                EndPoint = rotate,
                Stroke = new SolidColorBrush(Color.Parse("#B3E5A44C")),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            NoteHandleLayer.Children.Add(stem);

            var knob = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.Parse("#FFE5A44C")),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(knob, rotate.X - 4.5);
            Canvas.SetTop(knob, rotate.Y - 4.5);
            NoteHandleLayer.Children.Add(knob);
        }

        NoteHandleLayer.IsVisible = true;
    }

    /// <summary>旋转中心；气泡用矩形中心，和导出保持一致（不含尾巴）。</summary>
    private static Point NoteRotationCenter(IAnnotation item) => item switch
    {
        SpeechBubbleAnnotation bubble => bubble.Rect.Center,
        _ => NoteBounds(item).Center
    };

    /// <summary>标注在选区坐标系里的包围盒；选中框和命中测试共用。</summary>
    private static Rect NoteBounds(IAnnotation item)
    {        switch (item)
        {
            case ShapeAnnotation shape:
                return shape.Rect;

            case ArrowAnnotation arrow:
                return FromPoints(arrow.From, arrow.To);

            case LineAnnotation line:
                return FromPoints(line.From, line.To);

            case DistanceAnnotation distance:
                return FromPoints(distance.From, distance.To);

            case AngleAnnotation angle:
                return FromPoints([angle.Vertex, angle.A, angle.B]);

            case BracketAnnotation bracket:
                return bracket.Rect;

            case SpeechBubbleAnnotation bubble:
                return FromPoints(
                    [bubble.Rect.TopLeft, bubble.Rect.BottomRight, bubble.Tail]);

            case PenAnnotation { Points.Count: > 0 } pen:
                return FromPoints(pen.Points);

            case MosaicAnnotation mosaic:
                return mosaic.Rect;

            case SpotlightAnnotation spotlight:
                return spotlight.Rect;

            case MagnifierAnnotation magnifier:
                return magnifier.Rect;

            case WatermarkAnnotation watermark:
                return watermark.Area;

            case TextAnnotation text:
                // 没有排版信息，按字宽粗估：中日韩一个字号宽，西文约 0.6
                return new Rect(
                    text.Origin.X,
                    text.Origin.Y - text.FontSize,
                    EstimateTextWidth(text.Text, text.FontSize),
                    text.FontSize * 1.25);

            case NumberAnnotation number:
                return new Rect(
                    number.Center.X - number.Size / 2,
                    number.Center.Y - number.Size / 2,
                    number.Size,
                    number.Size);

            default:
                return default;
        }
    }

    private static double EstimateTextWidth(string text, double fontSize)
    {
        var width = 0.0;

        foreach (var ch in text)
            width += ch > 0x2E7F ? fontSize : fontSize * 0.6;

        return Math.Max(fontSize, width);
    }

    private static Rect Grow(Rect rect, double amount) => new(
        rect.X - amount,
        rect.Y - amount,
        Math.Max(0, rect.Width + amount * 2),
        Math.Max(0, rect.Height + amount * 2));

    private static Rect FromPoints(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    private static Rect FromPoints(IReadOnlyList<Point> points)
    {
        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;

        foreach (var point in points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>点到线段的最短距离。</summary>
    private static double DistanceToSegment(Point point, Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared < 0.0001)
            return Distance(point, from);

        var t = Math.Clamp(((point.X - from.X) * dx + (point.Y - from.Y) * dy) / lengthSquared, 0, 1);

        return Distance(point, new Point(from.X + dx * t, from.Y + dy * t));
    }

    /// <summary>数字键 → 工具；和按钮 ToolTip 里的数字保持一致。L 给直线。</summary>
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
            Key.L => AnnotationTool.Line,
            Key.D => AnnotationTool.Distance,
            Key.E => AnnotationTool.Eraser,
            Key.K => AnnotationTool.Spotlight,
            Key.Z => AnnotationTool.Magnifier,
            Key.W => AnnotationTool.Watermark,
            Key.Q => AnnotationTool.Bubble,
            Key.OemOpenBrackets => AnnotationTool.Bracket,
            _ => AnnotationTool.None
        };

        return tool != AnnotationTool.None;
    }

    /// <summary>选区变化或重设时清空标注：坐标相对选区，选区一动旧标注就错位。</summary>
    private void ClearAnnotations()
    {
        HideTextEditor();
        MarkNoteMutation();
        CancelAngleDraft();

        if (_notes.Items.Count == 0 && _pendingAnnotation is null)
            return;

        _notes.Clear();
        _selectedNote = -1;
        _draggingNote = -1;
        _noteDragOriginal = null;
        _pendingAnnotation = null;
        _penPoints = null;
        _nextNumber = 1;
        _notesBeforeDrag = null;
        RebuildNotePreview();
        UpdateToolButtons();
    }

    private void SetTool(AnnotationTool tool)
    {
        var next = _tool == tool ? AnnotationTool.None : tool;

        if (next != AnnotationTool.Angle)
            CancelAngleDraft();

        _tool = next;

        // 切到画图工具时取消选中，避免虚线框挡着下一笔
        if (_tool != AnnotationTool.None)
            SelectNote(-1);

        UpdateToolButtons();
    }

    /// <summary>直接设成某个工具（不做「再点一次取消」的切换语义），用于临时让位。</summary>
    private void ForceTool(AnnotationTool tool)
    {
        if (tool != AnnotationTool.Angle)
            CancelAngleDraft();

        _tool = tool;

        if (tool != AnnotationTool.None)
            SelectNote(-1);

        UpdateToolButtons();
    }

    // ---------- 工具组折叠（positions） ----------

    /// <summary>一个 position：多个工具折叠成一个触发按钮，入口是组内最后一个。</summary>
    private sealed class ToolGroup
    {
        public required Button Trigger { get; init; }
        public required Path Icon { get; init; }
        public required AnnotationTool[] Members { get; set; }
        public int EntryIndex { get; set; }
    }

    private readonly List<ToolGroup> _toolGroups = new();
    private DispatcherTimer? _groupHideTimer;
    private DispatcherTimer? _groupShowTimer;
    private ToolGroup? _pendingGroup;
    private ToolGroup? _activeGroup;
    private int _groupShowDelayMs = 120;
    private ToolbarLayout _layout = ToolbarLayout.Default();

    /// <summary>测试可调；0 = 立即显示。</summary>
    internal int ToolGroupShowDelayMs
    {
        get => _groupShowDelayMs;
        set => _groupShowDelayMs = Math.Max(0, value);
    }

    /// <summary>工具 → (几何资源键, 线宽补偿)；补偿值必须和 XAML 里那个按钮写的一致。</summary>
    private static readonly Dictionary<AnnotationTool, (string Key, double Thickness)> ToolIcons =
        new()
        {
            [AnnotationTool.Rectangle] = ("ToolRect", 1.2),
            [AnnotationTool.Ellipse] = ("ToolEllipse", 1.2),
            [AnnotationTool.Arrow] = ("ToolArrow", 1.053),
            [AnnotationTool.Line] = ("ToolLine", 1.05),
            [AnnotationTool.Distance] = ("ToolDistance", 1.2),
            [AnnotationTool.Angle] = ("ToolAngle", 1.2),
            [AnnotationTool.Bracket] = ("ToolBracket", 1.2),
            [AnnotationTool.Bubble] = ("ToolBubble", 1.2),
            [AnnotationTool.Pen] = ("ToolPen", 1.125),
            [AnnotationTool.Highlighter] = ("ToolHighlighter", 1.161),
            [AnnotationTool.Mosaic] = ("ToolMosaic", 1.2),
            [AnnotationTool.Watermark] = ("ToolWatermark", 1.2),
            [AnnotationTool.Text] = ("ToolText", 0.972),
            [AnnotationTool.Number] = ("ToolNumber", 1.2)
        };

    private void BuildToolGroups()
    {
        _toolGroups.Add(new ToolGroup
        {
            Trigger = ShapeGroupButton,
            Icon = ShapeGroupIcon,
            Members = [AnnotationTool.Ellipse, AnnotationTool.Bracket, AnnotationTool.Rectangle],
            EntryIndex = 2
        });
        _toolGroups.Add(new ToolGroup
        {
            Trigger = ArrowGroupButton,
            Icon = ArrowGroupIcon,
            Members = [AnnotationTool.Line, AnnotationTool.Distance, AnnotationTool.Angle,
                AnnotationTool.Arrow],
            EntryIndex = 3
        });
        _toolGroups.Add(new ToolGroup
        {
            Trigger = PenGroupButton,
            Icon = PenGroupIcon,
            Members = [AnnotationTool.Highlighter, AnnotationTool.Pen],
            EntryIndex = 1
        });
        _toolGroups.Add(new ToolGroup
        {
            Trigger = TextGroupButton,
            Icon = TextGroupIcon,
            Members = [AnnotationTool.Number, AnnotationTool.Watermark, AnnotationTool.Bubble,
                AnnotationTool.Text],
            EntryIndex = 3
        });

        foreach (var group in _toolGroups)
        {
            group.Trigger.Click += (_, _) =>
            {
                HideToolGroupPopover();
                ForceTool(group.Members[group.EntryIndex]);
            };
            group.Trigger.PointerEntered += (_, _) => ScheduleShowToolGroupPopover(group);
            group.Trigger.PointerExited += (_, _) =>
            {
                CancelPendingToolGroupPopover();
                ScheduleHideToolGroupPopover();
            };
            group.Trigger.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Down)
                    return;

                ShowToolGroupPopover(group);
                if (ToolGroupOptions.Children.Count > 0
                    && ToolGroupOptions.Children[0] is Button entry)
                {
                    entry.Focus();
                }

                e.Handled = true;
            };
        }

        ToolGroupPopover.PointerEntered += (_, _) =>
        {
            _groupHideTimer?.Stop();
            CancelPendingToolGroupPopover();
        };
        ToolGroupPopover.PointerExited += (_, _) => ScheduleHideToolGroupPopover();
        ToolGroupPopover.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            var group = _activeGroup;
            HideToolGroupPopover();
            group?.Trigger.Focus();
            e.Handled = true;
        };
        ToolGroupPopover.LostFocus += (_, _) =>
        {
            if (!ToolGroupPopover.IsKeyboardFocusWithin)
                HideToolGroupPopover();
        };
    }

    private void ScheduleShowToolGroupPopover(ToolGroup group)
    {
        _groupHideTimer?.Stop();
        _groupShowTimer?.Stop();
        _pendingGroup = group;

        if (_groupShowDelayMs <= 0)
        {
            ShowToolGroupPopover(group);
            return;
        }

        _groupShowTimer ??= new DispatcherTimer();
        _groupShowTimer.Interval = TimeSpan.FromMilliseconds(_groupShowDelayMs);
        _groupShowTimer.Tick -= OnGroupShowTimerTick;
        _groupShowTimer.Tick += OnGroupShowTimerTick;
        _groupShowTimer.Start();
    }

    private void OnGroupShowTimerTick(object? sender, EventArgs e)
    {
        _groupShowTimer?.Stop();

        if (_pendingGroup is { } group)
            ShowToolGroupPopover(group);
    }

    private void CancelPendingToolGroupPopover()
    {
        _groupShowTimer?.Stop();
        _pendingGroup = null;
    }

    /// <summary>按当前布局重建工具区：单成员显示普通按钮，多成员显示折叠组。</summary>
    private void RenderToolbar()
    {
        ToolButtons.Children.Clear();

        foreach (var position in _layout.Positions)
        {
            var tools = position.Where(_layout.IsVisible).ToList();
            if (tools.Count == 0)
                continue;

            if (tools.Count == 1)
            {
                if (ToolButton(tools[0]) is { } single)
                {
                    single.IsVisible = true;
                    ToolButtons.Children.Add(single);
                }

                continue;
            }

            var groupIndex = ToolbarLayout.GroupIndexFor(tools[0]);
            var sameGroup = groupIndex >= 0
                && tools.All(tool => ToolbarLayout.GroupIndexFor(tool) == groupIndex);

            if (sameGroup)
            {
                var group = _toolGroups[groupIndex];
                group.Members = tools.ToArray();
                group.EntryIndex = group.Members.Length - 1;
                group.Trigger.IsVisible = true;
                ToolTip.SetTip(
                    group.Trigger,
                    $"{ToolbarLayout.GroupLabel(groupIndex)}：{string.Join(" / ", tools.Select(ToolName))}");
                ToolButtons.Children.Add(group.Trigger);
            }
            else
            {
                // 兜底：跨组的手改配置拆成独立按钮
                foreach (var tool in tools)
                {
                    if (ToolButton(tool) is not { } button)
                        continue;

                    button.IsVisible = true;
                    ToolButtons.Children.Add(button);
                }
            }
        }

        foreach (var tool in Enum.GetValues<AnnotationTool>())
        {
            if (ToolButton(tool) is { } button && !ToolButtons.Children.Contains(button))
                button.IsVisible = false;
        }

        foreach (var group in _toolGroups)
        {
            if (!ToolButtons.Children.Contains(group.Trigger))
                group.Trigger.IsVisible = false;
        }

        UpdateToolButtons();
    }

    private void ApplyToolbarLayout(ToolbarLayout layout)
    {
        _layout = layout;
        RenderToolbar();
    }

    /// <summary>测试和编辑器共用：把工具设成所在折叠组的入口。</summary>
    internal void SetToolEntry(AnnotationTool tool)
    {
        _layout.SetEntry(tool);
        RenderToolbar();
    }

    private Button? ToolButton(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle => RectToolButton,
        AnnotationTool.Ellipse => EllipseToolButton,
        AnnotationTool.Bracket => BracketToolButton,
        AnnotationTool.Arrow => ArrowToolButton,
        AnnotationTool.Line => LineToolButton,
        AnnotationTool.Distance => DistanceToolButton,
        AnnotationTool.Angle => AngleToolButton,
        AnnotationTool.Pen => PenToolButton,
        AnnotationTool.Highlighter => HighlighterToolButton,
        AnnotationTool.Mosaic => MosaicToolButton,
        AnnotationTool.Eraser => EraserToolButton,
        AnnotationTool.Spotlight => SpotlightToolButton,
        AnnotationTool.Magnifier => MagnifierToolButton,
        AnnotationTool.Number => NumberToolButton,
        AnnotationTool.Text => TextToolButton,
        AnnotationTool.Watermark => WatermarkToolButton,
        AnnotationTool.Bubble => BubbleToolButton,
        _ => null
    };

    /// <summary>右键拖拽把手：工具显示、组入口、分组顺序、恢复默认。</summary>
    private void ShowToolLayoutMenu()
    {
        var menu = new ContextMenu();

        var visibility = new MenuItem { Header = "显示工具" };
        foreach (var tool in ToolbarLayout.AllTools())
        {
            var item = new MenuItem
            {
                Header = ToolName(tool),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _layout.IsVisible(tool)
            };

            item.Click += (_, _) =>
            {
                _layout.Toggle(tool);
                RenderToolbar();
            };

            visibility.Items.Add(item);
        }

        menu.Items.Add(visibility);

        var entries = new MenuItem { Header = "组入口" };
        for (var groupIndex = 0; groupIndex < ToolbarLayout.Groups.Length; groupIndex++)
        {
            var group = new MenuItem { Header = ToolbarLayout.GroupLabel(groupIndex) };

            foreach (var tool in ToolbarLayout.Groups[groupIndex])
            {
                var position = _layout.Positions.FirstOrDefault(item => item.Contains(tool));
                var item = new MenuItem
                {
                    Header = ToolName(tool),
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = position is not null && position[^1] == tool
                };

                item.Click += (_, _) =>
                {
                    _layout.SetEntry(tool);
                    RenderToolbar();
                };

                group.Items.Add(item);
            }

            entries.Items.Add(group);
        }

        menu.Items.Add(entries);

        var order = new MenuItem { Header = "分组顺序" };
        for (var index = 0; index < _layout.Positions.Count; index++)
        {
            var position = _layout.Positions[index];
            if (position.Count == 0)
                continue;

            var groupIndex = ToolbarLayout.GroupIndexFor(position[0]);
            var label = groupIndex >= 0
                && position.All(tool => ToolbarLayout.GroupIndexFor(tool) == groupIndex)
                    ? ToolbarLayout.GroupLabel(groupIndex)
                    : ToolName(position[0]);

            var entry = new MenuItem { Header = label };
            var up = new MenuItem { Header = "上移" };
            var down = new MenuItem { Header = "下移" };
            var captured = index;

            up.Click += (_, _) =>
            {
                _layout.MovePosition(captured, -1);
                RenderToolbar();
            };
            down.Click += (_, _) =>
            {
                _layout.MovePosition(captured, +1);
                RenderToolbar();
            };

            entry.Items.Add(up);
            entry.Items.Add(down);
            order.Items.Add(entry);
        }

        menu.Items.Add(order);
        menu.Items.Add(new Separator());

        var reset = new MenuItem { Header = "恢复默认" };
        reset.Click += (_, _) => ApplyToolbarLayout(ToolbarLayout.Default());
        menu.Items.Add(reset);

        menu.Open(ToolbarDragHandle);
    }

    /// <summary>展开某个工具的选项条，摆在触发按钮正上方。</summary>
    private void ShowToolGroupPopover(ToolGroup group)
    {
        _groupHideTimer?.Stop();
        _groupShowTimer?.Stop();
        _pendingGroup = null;
        _activeGroup = group;
        ToolGroupOptions.Children.Clear();

        // 反序：入口在最左（参考 stackPresentation 把 popoverItemIds 反了过来）
        for (var i = group.Members.Length - 1; i >= 0; i--)
        {
            var tool = group.Members[i];
            var option = new Button { Focusable = true };
            option.Classes.Add("tool");

            var icon = new Path();
            ApplyToolIcon(icon, tool);
            option.Content = icon;
            ToolTip.SetTip(option, ToolName(tool));
            SetSelected(option, tool == group.Members[group.EntryIndex]);

            option.Click += (_, _) =>
            {
                group.EntryIndex = Array.IndexOf(group.Members, tool);
                HideToolGroupPopover();
                ForceTool(tool);
            };

            ToolGroupOptions.Children.Add(option);
        }

        ToolGroupPopover.IsVisible = true;

        // 尺寸按内容算：32 的按钮 + 2 的间距 + Border 的 padding 4×2 与边框 1×2
        var width = group.Members.Length * 32 + (group.Members.Length - 1) * 2 + 10;
        var height = 32 + 10;

        var origin = group.Trigger.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        // 与触发按钮左对齐：比居中更稳定，也不会因为按钮宽度取不到而左右跳
        var left = Math.Clamp(origin.X, 8, Math.Max(8, Width - width - 8));
        var top = Math.Max(8, origin.Y - height - 6);

        Canvas.SetLeft(ToolGroupPopover, left);
        Canvas.SetTop(ToolGroupPopover, top);
    }

    private void ScheduleHideToolGroupPopover()
    {
        if (_groupHideTimer is null)
        {
            _groupHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _groupHideTimer.Tick += (_, _) =>
            {
                _groupHideTimer!.Stop();
                HideToolGroupPopover();
            };
        }

        _groupHideTimer.Stop();
        _groupHideTimer.Start();
    }

    private void HideToolGroupPopover()
    {
        _groupHideTimer?.Stop();
        _groupShowTimer?.Stop();
        _pendingGroup = null;
        _activeGroup = null;
        ToolGroupPopover.IsVisible = false;
        ToolGroupOptions.Children.Clear();
    }

    private void ApplyToolIcon(Path icon, AnnotationTool tool)
    {
        if (!ToolIcons.TryGetValue(tool, out var info))
            return;

        if (this.TryFindResource(info.Key, out var resource) && resource is Geometry geometry)
        {
            icon.Data = geometry;
            icon.StrokeThickness = info.Thickness;
        }
    }

    private static string ToolName(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle => "矩形",
        AnnotationTool.Ellipse => "椭圆",
        AnnotationTool.Pen => "画笔",
        AnnotationTool.Highlighter => "高亮",
        AnnotationTool.Text => "文字",
        AnnotationTool.Number => "序号",
        AnnotationTool.Arrow => "箭头",
        AnnotationTool.Line => "直线",
        AnnotationTool.Eraser => "橡皮擦",
        AnnotationTool.Spotlight => "聚光灯",
        AnnotationTool.Magnifier => "放大镜",
        AnnotationTool.Watermark => "水印",
        AnnotationTool.Distance => "测距",
        AnnotationTool.Bracket => "括号",
        AnnotationTool.Bubble => "气泡",
        AnnotationTool.Angle => "角度",
        _ => string.Empty
    };

    private void UpdateToolButtons()
    {
        InvalidateToolbarMetrics();
        SetSelected(SelectToolButton, _tool == AnnotationTool.None);
        SetSelected(RectToolButton, _tool == AnnotationTool.Rectangle);
        SetSelected(EllipseToolButton, _tool == AnnotationTool.Ellipse);
        SetSelected(ArrowToolButton, _tool == AnnotationTool.Arrow);
        SetSelected(LineToolButton, _tool == AnnotationTool.Line);
        SetSelected(DistanceToolButton, _tool == AnnotationTool.Distance);
        SetSelected(BracketToolButton, _tool == AnnotationTool.Bracket);
        SetSelected(BubbleToolButton, _tool == AnnotationTool.Bubble);
        SetSelected(AngleToolButton, _tool == AnnotationTool.Angle);
        SetSelected(EraserToolButton, _tool == AnnotationTool.Eraser);
        SetSelected(SpotlightToolButton, _tool == AnnotationTool.Spotlight);
        SetSelected(MagnifierToolButton, _tool == AnnotationTool.Magnifier);
        SetSelected(PenToolButton, _tool == AnnotationTool.Pen);
        SetSelected(HighlighterToolButton, _tool == AnnotationTool.Highlighter);
        SetSelected(MosaicToolButton, _tool == AnnotationTool.Mosaic);
        SetSelected(TextToolButton, _tool == AnnotationTool.Text);
        SetSelected(NumberToolButton, _tool == AnnotationTool.Number);
        SetSelected(WatermarkToolButton, _tool == AnnotationTool.Watermark);
        UndoButton.IsEnabled = _notes.CanUndo || _selectionUndo.Count > 0;
        RedoButton.IsEnabled = _notes.CanRedo || _selectionRedo.Count > 0;

        // 折叠组：入口跟着当前工具走，按钮图标显示组里正在用的那个
        foreach (var group in _toolGroups)
        {
            var active = Array.IndexOf(group.Members, _tool);
            if (active >= 0)
                group.EntryIndex = active;

            ApplyToolIcon(group.Icon, active >= 0 ? _tool : group.Members[group.EntryIndex]);
            SetSelected(group.Trigger, active >= 0);
        }

        // 属性行只在选中标注工具时出现；橡皮擦/放大镜没有可调属性，整条让出去
        StyleRow.IsVisible = _tool is not (
            AnnotationTool.None or AnnotationTool.Eraser or AnnotationTool.Magnifier);

        // 每组属性只在当前工具用得上时出现（LumaShot 的属性卡思路）
        var drawing = _tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
            or AnnotationTool.Arrow or AnnotationTool.Line or AnnotationTool.Distance
            or AnnotationTool.Angle or AnnotationTool.Bracket or AnnotationTool.Bubble
            or AnnotationTool.Pen or AnnotationTool.Highlighter;

        ColorGroup.IsVisible = _tool is not (
            AnnotationTool.None or AnnotationTool.Mosaic or AnnotationTool.Spotlight
            or AnnotationTool.Eraser);
        WidthGroup.IsVisible = drawing;
        DashGroup.IsVisible = _tool is AnnotationTool.Rectangle or AnnotationTool.Arrow
            or AnnotationTool.Line or AnnotationTool.Distance or AnnotationTool.Bracket
            or AnnotationTool.Pen or AnnotationTool.Highlighter;
        FillGroup.IsVisible = _tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
            or AnnotationTool.Bubble;
        if (FillGroup.IsVisible)
        {
            var activeFill = _tool == AnnotationTool.Bubble ? _bubbleFill : _noteFill;
            SetSelected(FillNoneButton, activeFill == ShapeFill.None);
            SetSelected(FillSoftButton, activeFill == ShapeFill.Soft);
            SetSelected(FillSolidButton, activeFill == ShapeFill.Solid);
        }
        OpacityGroup.IsVisible = (drawing && _tool is not (AnnotationTool.Bubble or AnnotationTool.Angle))
            || _tool is AnnotationTool.Spotlight or AnnotationTool.Watermark;

        // 聚光灯、水印和普通标注共用同一个滑块，但各存各的字段
        if (OpacityGroup.IsVisible)
        {
            var opacity = _tool switch
            {
                AnnotationTool.Spotlight => _spotlightOpacity,
                AnnotationTool.Watermark => _watermarkOpacity,
                _ => _noteOpacity
            };
            OpacitySlider.Value = Math.Round(opacity * 100);
            OpacityValue.Text = $"{opacity * 100:0}%";
        }
        RadiusGroup.IsVisible = _tool == AnnotationTool.Rectangle;

        // 水印和文字共用字号档位；加粗只对文字有意义
        FontGroup.IsVisible = _tool is AnnotationTool.Text or AnnotationTool.Watermark
            or AnnotationTool.Bubble;
        BoldButton.IsVisible = _tool == AnnotationTool.Text;
        if (FontGroup.IsVisible)
        {
            var fontSize = ActiveFontSize;
            SetSelected(FontSButton, Math.Abs(fontSize - 14) < 0.1);
            SetSelected(FontMButton, Math.Abs(fontSize - 16) < 0.1);
            SetSelected(FontLButton, Math.Abs(fontSize - 20) < 0.1);
            SetSelected(FontXlButton, Math.Abs(fontSize - 32) < 0.1);
        }
        NumberGroup.IsVisible = _tool == AnnotationTool.Number;
        MosaicGroup.IsVisible = _tool == AnnotationTool.Mosaic;

        // 属性行显隐会改变工具条宽度：先让它重新测量，再重新居中（用户拖过就不再跟随）
        ActionBar.InvalidateMeasure();
        MeasureActionBar();

        if (ActionBar.IsVisible && !_toolbarManual)
            PositionActionBar(_selection);
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
                    _tool == AnnotationTool.Ellipse,
                    _noteOpacity,
                    _noteFill,
                    _noteRadius,
                    _noteDash);
                break;

            case AnnotationTool.Arrow:
                _pendingAnnotation = new ArrowAnnotation(
                    _annotationStart,
                    _annotationStart,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _noteOpacity,
                    _noteDash);
                break;

            case AnnotationTool.Line:
                _pendingAnnotation = new LineAnnotation(
                    _annotationStart,
                    _annotationStart,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _noteOpacity,
                    _noteDash);
                break;

            case AnnotationTool.Distance:
                _pendingAnnotation = new DistanceAnnotation(
                    _annotationStart,
                    _annotationStart,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    FormatDistance(_annotationStart, _annotationStart),
                    _noteOpacity,
                    _noteDash);
                break;

            case AnnotationTool.Pen:
                _penPoints = [_annotationStart];
                _pendingAnnotation = new PenAnnotation(
                    _penPoints,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _noteOpacity,
                    _noteDash);
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
                    new Rect(_annotationStart, new Size()), _mosaicCell, _mosaicBlur);
                break;

            case AnnotationTool.Spotlight:
                // 聚光灯固定用黑色压暗：用当前标注色会得到“琥珀高亮”，不是压暗
                _pendingAnnotation = new SpotlightAnnotation(
                    new Rect(_annotationStart, new Size()),
                    0xFF000000,
                    _spotlightOpacity);
                break;

            case AnnotationTool.Magnifier:
                _pendingAnnotation = new MagnifierAnnotation(
                    new Rect(_annotationStart, new Size()));
                break;

            case AnnotationTool.Watermark:
                _pendingAnnotation = new WatermarkAnnotation(
                    new Rect(_annotationStart, new Size()),
                    _watermarkText,
                    _noteColor.ToUInt32(),
                    _watermarkFontSize,
                    _watermarkOpacity,
                    WatermarkAngle,
                    WatermarkSpacing);
                break;

            case AnnotationTool.Bracket:
                _pendingAnnotation = new BracketAnnotation(
                    new Rect(_annotationStart, new Size()),
                    true,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _noteOpacity,
                    _noteDash);
                break;

            case AnnotationTool.Bubble:
                _pendingAnnotation = new SpeechBubbleAnnotation(
                    new Rect(_annotationStart, new Size()),
                    _annotationStart,
                    string.Empty,
                    _noteColor.ToUInt32(),
                    _noteWidth,
                    _bubbleFontSize,
                    _bubbleFill);
                break;

            case AnnotationTool.Text:
                ShowTextEditor(_annotationStart);
                return true;

            case AnnotationTool.Number:
                MarkNoteMutation();
                _notes.Add(new NumberAnnotation(
                    _annotationStart, _nextNumber++, _noteColor.ToUInt32(), _numberSize));
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
            LineAnnotation line => line with { To = current },
            MosaicAnnotation mosaic => mosaic with { Rect = NormalizeRect(_annotationStart, current) },
            SpotlightAnnotation spotlight => spotlight with
            {
                Rect = NormalizeRect(_annotationStart, current)
            },
            MagnifierAnnotation magnifier => magnifier with
            {
                Rect = NormalizeRect(_annotationStart, current)
            },
            WatermarkAnnotation watermark => watermark with
            {
                Area = NormalizeRect(_annotationStart, current)
            },
            DistanceAnnotation distance => distance with
            {
                To = current,
                Label = FormatDistance(distance.From, current)
            },
            AngleAnnotation angle => angle with
            {
                B = current,
                Label = FormatAngle(angle.Vertex, angle.A, current)
            },
            BracketAnnotation bracket => bracket with
            {
                Rect = NormalizeRect(_annotationStart, current),
                OpenRight = current.X >= _annotationStart.X
            },
            SpeechBubbleAnnotation bubble => bubble with
            {
                Rect = NormalizeRect(_annotationStart, current),
                Tail = AnnotationGeometry.BubbleTail(
                    NormalizeRect(_annotationStart, current), _annotationStart)
            },
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

        // 测距标签在提交时按当前单位定稿，之后切单位不回溯
        if (annotation is DistanceAnnotation measured)
            annotation = measured with { Label = FormatDistance(measured.From, measured.To) };

        // 水印单击（没拖出区域）= 铺满整个选区
        if (annotation is WatermarkAnnotation clicked &&
            (clicked.Area.Width < 8 || clicked.Area.Height < 8))
        {
            annotation = clicked with
            {
                Area = new Rect(0, 0, _selection.Width, _selection.Height)
            };
        }

        // 点一下没拖动：当误触丢掉，不进撤销栈
        var keep = annotation switch
        {
            ShapeAnnotation shape => shape.Rect.Width >= 2 && shape.Rect.Height >= 2,
            ArrowAnnotation arrow => Distance(arrow.From, arrow.To) >= 6,
            LineAnnotation line => Distance(line.From, line.To) >= 6,
            PenAnnotation pen => pen.Points.Count >= 2,
            MosaicAnnotation mosaic => mosaic.Rect.Width >= 4 && mosaic.Rect.Height >= 4,
            SpotlightAnnotation spotlight => spotlight.Rect.Width >= 8 && spotlight.Rect.Height >= 8,
            MagnifierAnnotation magnifier => magnifier.Rect.Width >= 8 && magnifier.Rect.Height >= 8,
            WatermarkAnnotation watermark => watermark.Area.Width >= 8 && watermark.Area.Height >= 8,
            DistanceAnnotation distance => Distance(distance.From, distance.To) >= 6,
            BracketAnnotation bracket => bracket.Rect.Width >= 8 && bracket.Rect.Height >= 8,
            SpeechBubbleAnnotation bubble => bubble.Rect.Width >= 8 && bubble.Rect.Height >= 8,
            _ => false
        };

        if (keep)
        {
            _notes.Add(annotation);
            MarkNoteMutation();
        }

        RebuildNotePreview();
        UpdateToolButtons();
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

        /// <summary>绕 center 旋转 point（度，屏幕坐标，顺时针为正）。</summary>
    private static Point RotatePoint(Point point, Point center, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;

        return new Point(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);
    }

    private static Point Direction(Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < 0.001 ? new Point(1, 0) : new Point(dx / length, dy / length);
    }

    private static Point Bisector(Point a, Point b)
    {
        var x = a.X + b.X;
        var y = a.Y + b.Y;
        var length = Math.Sqrt(x * x + y * y);
        return length < 0.001 ? new Point(-a.Y, a.X) : new Point(x / length, y / length);
    }

    /// <summary>测距标签：跟随选区工具条的 px/DIP 单位，创建时定稿。</summary>
    private string FormatDistance(Point from, Point to)
    {
        var dip = Distance(from, to);
        return _sizeInPixels ? $"{dip * _scaling:0} px" : $"{dip:0} DIP";
    }

    /// <summary>角度第一步：按下开始第一条边；草稿已存在时开始第二条边。</summary>
    private void BeginAngleDrag(Point point)
    {
        _annotationStart = new Point(point.X - _selection.X, point.Y - _selection.Y);
        _penPoints = null;

        if (_angleDraft.IsActive)
        {
            _anglePhase = AnglePhase.Second;
            _pendingAnnotation = new AngleAnnotation(
                _angleDraft.Vertex!.Value,
                _angleDraft.First!.Value,
                _annotationStart,
                _noteColor.ToUInt32(),
                _noteWidth,
                FormatAngle(_angleDraft.Vertex.Value, _angleDraft.First.Value, _annotationStart),
                _noteOpacity);
        }
        else
        {
            _anglePhase = AnglePhase.First;
            _pendingAnnotation = new LineAnnotation(
                _annotationStart,
                _annotationStart,
                _noteColor.ToUInt32(),
                _noteWidth,
                _noteOpacity,
                _noteDash);
        }

        RebuildNotePreview();
    }

    /// <summary>角度松手：第一段只存草稿，第二段才真正提交标注。</summary>
    private void CommitAngleDrag()
    {
        var annotation = _pendingAnnotation;
        _pendingAnnotation = null;
        _penPoints = null;
        var phase = _anglePhase;
        _anglePhase = AnglePhase.None;

        if (phase == AnglePhase.First)
        {
            if (annotation is LineAnnotation { } line && Distance(line.From, line.To) >= 6)
            {
                _angleDraft.Begin(line.From, line.To);
                ShowAngleHint();
            }
            else
            {
                _angleDraft.Clear();
                HideAngleHint();
            }
        }
        else if (phase == AnglePhase.Second && annotation is AngleAnnotation pending)
        {
            if (_angleDraft.TryBuild(pending.B, out _))
            {
                _notes.Add(pending with
                {
                    Label = FormatAngle(pending.Vertex, pending.A, pending.B)
                });
                MarkNoteMutation();
                _angleDraft.Clear();
                HideAngleHint();
            }
            else
            {
                // 第二条边太短：保留草稿，用户可以再拖一次
                ShowAngleHint();
            }
        }

        RebuildNotePreview();
        UpdateToolButtons();
    }

    /// <summary>清掉角度草稿；Esc / 右键 / 切工具共用。</summary>
    private void CancelAngleDraft()
    {
        if (!_angleDraft.IsActive && _anglePhase == AnglePhase.None)
            return;

        _angleDraft.Clear();
        _anglePhase = AnglePhase.None;
        _pendingAnnotation = null;
        _penPoints = null;
        HideAngleHint();
        RebuildNotePreview();
        UpdateToolButtons();
    }

    private static string FormatAngle(Point vertex, Point a, Point b) =>
        $"{AnnotationGeometry.AngleBetween(vertex, a, b):0}°";

    private void ShowAngleHint()
    {
        AngleHint.Measure(Size.Infinity);
        Canvas.SetLeft(AngleHint, Math.Max(8, (Width - AngleHint.DesiredSize.Width) / 2));
        AngleHint.IsVisible = true;
    }

    private void HideAngleHint() => AngleHint.IsVisible = false;

    /// <summary>把已提交的标注 + 正在画的那条重画到预览层。</summary>
    private void RebuildNotePreview()
    {
        NoteLayer.Children.Clear();
        NoteHandleLayer.Children.Clear();
        NoteHandleLayer.IsVisible = false;

        // 撤销 / 删除之后索引可能越界
        if (_selectedNote >= _notes.Items.Count)
            _selectedNote = -1;

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

        // 选中态：虚线外框，告诉用户现在拖的是哪一条
        if (_selectedNote >= 0 && _selectedNote < _notes.Items.Count)
        {
            var selected = _notes.Items[_selectedNote];
            AddNoteSelectionOutline(
                NoteBounds(selected),
                NoteRotationCenter(selected),
                AnnotationGeometry.RotationOf(selected));
        }

        UpdateNoteHandles();
    }

    private void AddNoteSelectionOutline(Rect bounds, Point center, double rotation = 0)
    {
        var outline = new Rectangle
        {
            Width = bounds.Width + 6,
            Height = bounds.Height + 6,
            Stroke = new SolidColorBrush(Color.Parse("#FFE5A44C")),
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
            IsHitTestVisible = false
        };

        if (Math.Abs(rotation) > 0.01)
        {
            var left = bounds.X - 3;
            var top = bounds.Y - 3;
            var width = bounds.Width + 6;
            var height = bounds.Height + 6;

            outline.RenderTransformOrigin = new RelativePoint(
                (center.X - left) / Math.Max(1, width),
                (center.Y - top) / Math.Max(1, height),
                RelativeUnit.Relative);
            outline.RenderTransform = new RotateTransform(rotation);
        }

        Canvas.SetLeft(outline, bounds.X - 3);
        Canvas.SetTop(outline, bounds.Y - 3);
        NoteLayer.Children.Add(outline);
    }

    private void AddPreviewShape(IAnnotation item)
    {
        var start = NoteLayer.Children.Count;

        switch (item)
        {
            case ShapeAnnotation { Ellipse: true } shape:
                var ellipse = new Ellipse
                {
                    Width = shape.Rect.Width,
                    Height = shape.Rect.Height,
                    Stroke = new SolidColorBrush(Color.FromUInt32(shape.Color), shape.Opacity),
                    StrokeThickness = shape.Width,
                    Fill = shape.Fill == ShapeFill.None
                        ? null
                        : new SolidColorBrush(
                            Color.FromUInt32(shape.Color),
                            shape.Fill == ShapeFill.Solid ? shape.Opacity : shape.Opacity * 0.28)
                };
                ApplyDash(ellipse, shape.Dash);
                Canvas.SetLeft(ellipse, shape.Rect.X);
                Canvas.SetTop(ellipse, shape.Rect.Y);
                NoteLayer.Children.Add(ellipse);
                break;

            case ShapeAnnotation shape:
                var rectangle = new Rectangle
                {
                    Width = shape.Rect.Width,
                    Height = shape.Rect.Height,
                    RadiusX = shape.Radius,
                    RadiusY = shape.Radius,
                    Stroke = new SolidColorBrush(Color.FromUInt32(shape.Color), shape.Opacity),
                    StrokeThickness = shape.Width,
                    Fill = shape.Fill == ShapeFill.None
                        ? null
                        : new SolidColorBrush(
                            Color.FromUInt32(shape.Color),
                            shape.Fill == ShapeFill.Solid ? shape.Opacity : shape.Opacity * 0.28)
                };
                ApplyDash(rectangle, shape.Dash);
                Canvas.SetLeft(rectangle, shape.Rect.X);
                Canvas.SetTop(rectangle, shape.Rect.Y);
                NoteLayer.Children.Add(rectangle);
                break;

            case ArrowAnnotation arrow:
                var arrowBrush = new SolidColorBrush(Color.FromUInt32(arrow.Color), arrow.Opacity);
                AddPreviewLine(arrowBrush, arrow.From, arrow.To, arrow.Width, arrow.Dash);
                var (left, right) = ArrowGeometry.Head(
                    arrow.From, arrow.To, Math.Max(8, arrow.Width * 3.5));
                AddPreviewLine(arrowBrush, arrow.To, left, arrow.Width);
                AddPreviewLine(arrowBrush, arrow.To, right, arrow.Width);
                break;

            case LineAnnotation line:
                AddPreviewLine(
                    new SolidColorBrush(Color.FromUInt32(line.Color), line.Opacity),
                    line.From, line.To, line.Width, line.Dash);
                break;

            case DistanceAnnotation distance:
                AddDistancePreview(distance);
                break;

            case AngleAnnotation angle:
                AddAnglePreview(angle);
                break;

            case BracketAnnotation bracket:
                AddBracketPreview(bracket);
                break;

            case SpeechBubbleAnnotation bubble:
                AddBubblePreview(bubble);
                break;

            case SpotlightAnnotation spotlight:
            {
                // 整层压暗，只在区域处挖洞（和 DimLayer 同一个写法）
                var outer = new RectangleGeometry(
                    new Rect(0, 0, Math.Max(_selection.Width, 1), Math.Max(_selection.Height, 1)));
                Geometry hole = spotlight.Ellipse
                    ? new EllipseGeometry(spotlight.Rect)
                    : new RectangleGeometry(spotlight.Rect);

                NoteLayer.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole),
                    Fill = new SolidColorBrush(Color.FromUInt32(spotlight.Color), spotlight.Opacity),
                    IsHitTestVisible = false
                });
                break;
            }

            case MagnifierAnnotation magnifier:
            {
                var zoom = Math.Clamp(magnifier.Zoom, 1.2, 6);
                var radius = magnifier.Circle ? magnifier.Rect.Width / 2 : 2;
                var color = Color.FromUInt32(magnifier.BorderColor);

                // 预览只画镜片边框 + 倍数：放大画面在导出时按 source 采样，
                // 预览层拿不到「导出时的源图」，硬做容易和导出一致性出问题。
                var lens = new Border
                {
                    Width = magnifier.Rect.Width,
                    Height = magnifier.Rect.Height,
                    CornerRadius = new CornerRadius(radius),
                    BorderThickness = new Thickness(2),
                    BorderBrush = new SolidColorBrush(color),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(lens, magnifier.Rect.X);
                Canvas.SetTop(lens, magnifier.Rect.Y);
                NoteLayer.Children.Add(lens);

                var zoomLabel = new TextBlock
                {
                    Text = $"{zoom:0.#}×",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(color),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(zoomLabel, magnifier.Rect.X + 5);
                Canvas.SetTop(zoomLabel, magnifier.Rect.Y + 3);
                NoteLayer.Children.Add(zoomLabel);
                break;
            }

            case WatermarkAnnotation watermark:
                AddWatermarkPreview(watermark);
                break;

            case PenAnnotation { Points.Count: >= 2 } pen:
                var polyline = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromUInt32(pen.Color), pen.Opacity),
                    StrokeThickness = pen.Width,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Points = new List<Point>(pen.Points)
                };
                ApplyDash(polyline, pen.Dash);
                NoteLayer.Children.Add(polyline);
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
                    FontWeight = text.Bold ? FontWeight.Bold : FontWeight.Normal,
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

        var rotation = AnnotationGeometry.RotationOf(item);
        if (Math.Abs(rotation) > 0.01 && NoteLayer.Children.Count > start)
        {
            var added = new List<Control>();
            for (var index = NoteLayer.Children.Count - 1; index >= start; index--)
            {
                added.Insert(0, NoteLayer.Children[index]);
                NoteLayer.Children.RemoveAt(index);
            }

            var center = NoteRotationCenter(item);
            var container = new Canvas
            {
                Width = _selection.Width,
                Height = _selection.Height,
                RenderTransformOrigin = new RelativePoint(
                    center.X / Math.Max(1, _selection.Width),
                    center.Y / Math.Max(1, _selection.Height),
                    RelativeUnit.Relative),
                RenderTransform = new RotateTransform(rotation)
            };

            foreach (var child in added)
                container.Children.Add(child);

            NoteLayer.Children.Add(container);
        }
    }

    // ---------- 文字输入 ----------

    private void ShowTextEditor(Point origin)
    {
        _textEditTarget = TextEditTarget.NewText;
        _textEditNoteIndex = -1;
        _textOrigin = origin;
        TextNoteBox.Text = string.Empty;
        TextNoteBox.FontSize = _noteFontSize;
        TextNoteBox.FontWeight = _noteBold ? FontWeight.Bold : FontWeight.Normal;
        TextNoteBox.Foreground = new SolidColorBrush(_noteColor);
        Canvas.SetLeft(TextNoteBox, _selection.X + origin.X);
        Canvas.SetTop(TextNoteBox, _selection.Y + origin.Y);
        TextNoteBox.IsVisible = true;
        TextNoteBox.Focus();
    }

    /// <summary>改写已有标注里的文字（水印，后续气泡）；返回 false 表示索引或类型不支持。</summary>
    internal bool EditNoteText(int index)
    {
        if (index < 0 || index >= _notes.Items.Count)
            return false;

        var (origin, text, fontSize, color, bold) = _notes.Items[index] switch
        {
            WatermarkAnnotation watermark => (
                new Point(
                    Math.Clamp(watermark.Area.X + 8, 0, Math.Max(0, _selection.Width - 120)),
                    Math.Clamp(watermark.Area.Y + 8, 0, Math.Max(0, _selection.Height - 40))),
                watermark.Text,
                watermark.FontSize,
                Color.FromUInt32(watermark.Color),
                true),
            SpeechBubbleAnnotation bubble => (
                new Point(
                    Math.Clamp(bubble.Rect.X + 8, 0, Math.Max(0, _selection.Width - 120)),
                    Math.Clamp(bubble.Rect.Y + 6, 0, Math.Max(0, _selection.Height - 40))),
                bubble.Text,
                bubble.FontSize,
                Color.FromUInt32(bubble.Color),
                false),
            _ => (default(Point), string.Empty, 0d, default(Color), false)
        };

        if (fontSize <= 0)
            return false;

        _textEditTarget = TextEditTarget.Note;
        _textEditNoteIndex = index;
        _textOrigin = origin;
        TextNoteBox.Text = text;
        TextNoteBox.FontSize = fontSize;
        TextNoteBox.FontWeight = bold ? FontWeight.Bold : FontWeight.Normal;
        TextNoteBox.Foreground = new SolidColorBrush(color);
        Canvas.SetLeft(TextNoteBox, _selection.X + origin.X);
        Canvas.SetTop(TextNoteBox, _selection.Y + origin.Y);
        TextNoteBox.IsVisible = true;
        TextNoteBox.CaretIndex = text.Length;
        TextNoteBox.Focus();
        return true;
    }

    private void HideTextEditor()
    {
        TextNoteBox.IsVisible = false;
        TextNoteBox.Text = string.Empty;
        _textEditTarget = TextEditTarget.NewText;
        _textEditNoteIndex = -1;
    }

    internal void CommitTextNote()
    {
        if (!TextNoteBox.IsVisible)
            return;

        var text = TextNoteBox.Text?.Trim() ?? string.Empty;
        var target = _textEditTarget;
        var index = _textEditNoteIndex;
        var origin = _textOrigin;
        HideTextEditor();

        if (target == TextEditTarget.Note)
        {
            if (index >= 0 && index < _notes.Items.Count)
            {
                IAnnotation? updated = _notes.Items[index] switch
                {
                    WatermarkAnnotation watermark when text.Length > 0 => watermark with { Text = text },
                    WatermarkAnnotation watermark => watermark,
                    SpeechBubbleAnnotation bubble => bubble with { Text = text },
                    _ => null
                };

                if (updated is not null)
                {
                    _notes.ReplaceWithHistory(index, updated);
                    MarkNoteMutation();

                    // 下次新建水印沿用刚改过的文字
                    if (updated is WatermarkAnnotation watermark)
                        _watermarkText = watermark.Text;
                }
            }

            RebuildNotePreview();
            UpdateToolButtons();
            return;
        }

        if (text.Length == 0)
            return;

        // 编辑器左上角对齐，Skia 的 DrawText 用基线：往下让一个字高
        _notes.Add(new TextAnnotation(
            new Point(origin.X, origin.Y + _noteFontSize),
            text,
            _noteColor.ToUInt32(),
            _noteFontSize,
            _noteBold));
        MarkNoteMutation();

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

    /// <summary>水印预览：父层按区域裁剪，子层整体旋转，保证裁剪框始终是轴向矩形。</summary>
    private void AddWatermarkPreview(WatermarkAnnotation watermark)
    {
        if (string.IsNullOrWhiteSpace(watermark.Text) ||
            watermark.Area.Width < 4 || watermark.Area.Height < 4)
            return;

        var spacing = WatermarkLayout.FitSpacing(watermark.Area, watermark.Spacing);
        var brush = new SolidColorBrush(Color.FromUInt32(watermark.Color), watermark.Opacity);

        var clip = new Border
        {
            Width = watermark.Area.Width,
            Height = watermark.Area.Height,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(clip, watermark.Area.X);
        Canvas.SetTop(clip, watermark.Area.Y);

        var tiles = new Canvas
        {
            Width = watermark.Area.Width,
            Height = watermark.Area.Height,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RenderTransform = new RotateTransform(watermark.Angle)
        };

        foreach (var point in WatermarkLayout.Tiles(
                     watermark.Area, spacing, WatermarkLayout.PreviewCap))
        {
            var text = new TextBlock
            {
                Text = watermark.Text,
                FontSize = watermark.FontSize,
                FontWeight = FontWeight.Bold,
                Foreground = brush
            };
            text.Measure(Size.Infinity);
            Canvas.SetLeft(text, point.X - watermark.Area.X - text.DesiredSize.Width / 2);
            Canvas.SetTop(text, point.Y - watermark.Area.Y - text.DesiredSize.Height / 2);
            tiles.Children.Add(text);
        }

        clip.Child = tiles;
        NoteLayer.Children.Add(clip);
    }

    /// <summary>测距预览：线 + 两端刻度 + 中点上方标签，和导出同几何。</summary>
    private void AddDistancePreview(DistanceAnnotation distance)
    {
        var brush = new SolidColorBrush(Color.FromUInt32(distance.Color), distance.Opacity);
        AddPreviewLine(brush, distance.From, distance.To, distance.Width, distance.Dash);

        var dx = distance.To.X - distance.From.X;
        var dy = distance.To.Y - distance.From.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.01)
            return;

        var nx = -dy / length;
        var ny = dx / length;
        if (ny > 0)
        {
            nx = -nx;
            ny = -ny;
        }

        const double tick = 4;
        AddPreviewLine(
            brush,
            new Point(distance.From.X - nx * tick, distance.From.Y - ny * tick),
            new Point(distance.From.X + nx * tick, distance.From.Y + ny * tick),
            distance.Width);
        AddPreviewLine(
            brush,
            new Point(distance.To.X - nx * tick, distance.To.Y - ny * tick),
            new Point(distance.To.X + nx * tick, distance.To.Y + ny * tick),
            distance.Width);

        if (string.IsNullOrEmpty(distance.Label))
            return;

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B3000000")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = distance.Label,
                FontSize = 11,
                Foreground = Brushes.White
            }
        };

        pill.Measure(Size.Infinity);
        var centerX = (distance.From.X + distance.To.X) / 2 + nx * 12;
        var centerY = (distance.From.Y + distance.To.Y) / 2 + ny * 12;
        Canvas.SetLeft(pill, centerX - pill.DesiredSize.Width / 2);
        Canvas.SetTop(pill, centerY - pill.DesiredSize.Height / 2);
        NoteLayer.Children.Add(pill);
    }

    /// <summary>角度预览：两条射线 + 圆弧 + 弧外侧标签，圆弧半径随短边收缩。</summary>
    private void AddAnglePreview(AngleAnnotation angle)
    {
        var brush = new SolidColorBrush(Color.FromUInt32(angle.Color), angle.Opacity);
        AddPreviewLine(brush, angle.Vertex, angle.A, angle.Width);
        AddPreviewLine(brush, angle.Vertex, angle.B, angle.Width);

        var lengthA = Distance(angle.Vertex, angle.A);
        var lengthB = Distance(angle.Vertex, angle.B);
        var radius = Math.Min(36, Math.Min(lengthA, lengthB) * 0.7);
        if (radius < 6)
            return;

        var unitA = Direction(angle.Vertex, angle.A);
        var unitB = Direction(angle.Vertex, angle.B);
        var start = new Point(
            angle.Vertex.X + unitA.X * radius,
            angle.Vertex.Y + unitA.Y * radius);
        var end = new Point(
            angle.Vertex.X + unitB.X * radius,
            angle.Vertex.Y + unitB.Y * radius);
        var cross = unitA.X * unitB.Y - unitA.Y * unitB.X;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                0,
                false,
                cross >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
        }

        NoteLayer.Children.Add(new Path
        {
            Data = geometry,
            Stroke = brush,
            StrokeThickness = angle.Width,
            StrokeLineCap = PenLineCap.Flat,
            IsHitTestVisible = false
        });

        if (string.IsNullOrEmpty(angle.Label))
            return;

        var bisector = Bisector(unitA, unitB);
        var center = new Point(
            angle.Vertex.X + bisector.X * (radius + 18),
            angle.Vertex.Y + bisector.Y * (radius + 18));

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B3000000")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = angle.Label,
                FontSize = 11,
                Foreground = Brushes.White
            }
        };

        pill.Measure(Size.Infinity);
        Canvas.SetLeft(pill, center.X - pill.DesiredSize.Width / 2);
        Canvas.SetTop(pill, center.Y - pill.DesiredSize.Height / 2);
        NoteLayer.Children.Add(pill);
    }

    private void AddBracketPreview(BracketAnnotation bracket)
    {
        var points = AnnotationGeometry.BracketPoints(bracket);
        if (points.Length < 2)
            return;

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromUInt32(bracket.Color), bracket.Opacity),
            StrokeThickness = bracket.Width,
            StrokeLineCap = PenLineCap.Flat,
            StrokeJoin = PenLineJoin.Miter,
            Points = new List<Point>(points)
        };

        ApplyDash(polyline, bracket.Dash);
        NoteLayer.Children.Add(polyline);
    }

    /// <summary>气泡预览：尾三角（有填充时）→ 圆角矩形 → 尾两条边，顺序保证接缝被盖住。</summary>
    private void AddBubblePreview(SpeechBubbleAnnotation bubble)
    {
        var rect = bubble.Rect;
        if (rect.Width < 4 || rect.Height < 4)
            return;

        var color = Color.FromUInt32(bubble.Color);
        var fillAlpha = bubble.Fill == ShapeFill.Solid ? 1 : 0.28;
        var (first, second) = AnnotationGeometry.BubbleTailBase(rect, bubble.Tail);

        if (bubble.Fill != ShapeFill.None)
        {
            NoteLayer.Children.Add(new Polygon
            {
                Points = new List<Point> { first, bubble.Tail, second },
                Fill = new SolidColorBrush(color, fillAlpha),
                IsHitTestVisible = false
            });
        }

        var body = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            CornerRadius = new CornerRadius(Math.Min(10, Math.Min(rect.Width, rect.Height) / 2)),
            BorderThickness = new Thickness(bubble.Width),
            BorderBrush = new SolidColorBrush(color, bubble.Opacity),
            Background = bubble.Fill == ShapeFill.None
                ? null
                : new SolidColorBrush(color, fillAlpha),
            Padding = new Thickness(8, 6),
            IsHitTestVisible = false
        };

        if (!string.IsNullOrEmpty(bubble.Text))
        {
            body.Child = new TextBlock
            {
                Text = bubble.Text,
                FontSize = bubble.FontSize,
                Foreground = bubble.Fill == ShapeFill.Solid
                    ? Brushes.White
                    : new SolidColorBrush(color),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
        }

        Canvas.SetLeft(body, rect.X);
        Canvas.SetTop(body, rect.Y);
        NoteLayer.Children.Add(body);

        // 尾巴只画两条边，底边不画：接缝处由矩形边框盖住
        var stroke = new SolidColorBrush(color, bubble.Opacity);
        AddPreviewLine(stroke, first, bubble.Tail, bubble.Width);
        AddPreviewLine(stroke, bubble.Tail, second, bubble.Width);
    }

    private void AddPreviewLine(IBrush brush, Point from, Point to, double width, LineDash dash = LineDash.Solid)
    {
        var line = new Line
        {
            StartPoint = from,
            EndPoint = to,
            Stroke = brush,
            StrokeThickness = width,
            StrokeLineCap = PenLineCap.Round
        };

        ApplyDash(line, dash);
        NoteLayer.Children.Add(line);
    }

    /// <summary>预览层的虚线 / 点线：dash 长度按线宽倍数给，和导出保持一致。</summary>
    private static void ApplyDash(Shape shape, LineDash dash)
    {
        if (dash == LineDash.Solid)
            return;

        shape.StrokeDashArray = dash == LineDash.Dot ? [0.1, 1.8] : [3, 2];
    }

    private Rect ToDip(PixelRect bounds) => new(
        (bounds.X - _origin.X) / _scaling,
        (bounds.Y - _origin.Y) / _scaling,
        bounds.Width / _scaling,
        bounds.Height / _scaling);

    /// <summary>选区所在的显示器（DIP，相对截图窗口）；无头测试或取不到时返回 null。</summary>
    private Rect? ActiveScreenDip() => ScreenDipFor(_selectionOrigin);

    /// <summary>指定矩形中心的显示器（DIP，相对截图窗口）。</summary>
    private Rect? ScreenDipFor(Rect rect)
    {
        var center = new PixelPoint(
            _origin.X + (int)Math.Round((rect.X + rect.Width / 2) * _scaling),
            _origin.Y + (int)Math.Round((rect.Y + rect.Height / 2) * _scaling));

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

    /// <summary>导出前的合成：先画标注，再按需切圆角。译文视图下以译文图为底。</summary>
    private byte[] ComposeResult(byte[] png)
    {
        var basePng = _translatedPng is not null && !_showOriginalView ? _translatedPng : png;
        var composed = AnnotationRenderer.Compose(basePng, _notes.Items, _scaling, _ocrMarks);
        return AnnotationRenderer.RoundCorners(composed, _cornerRadius * _scaling);
    }

    private void OnSizeUnitClick(object? sender, RoutedEventArgs e) => ToggleSizeUnit();

    private void OnSizeLockClick(object? sender, RoutedEventArgs e) =>
        SetAspectLock(SizeLockButton.IsChecked == true);

    private void OnSizeBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSizeEdit(AxisFor(sender));
            Focus();   // 交还焦点，数字键继续用来切工具
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // 放弃这次输入，恢复成当前选区的尺寸
            UpdateSizeBadge(_selection);
            Focus();
            e.Handled = true;
        }
    }

    private void OnSizeBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // 点单位 / 锁按钮时先不提交：否则会按旧单位先换算一遍再切换
        var focused = FocusManager?.GetFocusedElement();
        if (ReferenceEquals(focused, SizeUnitButton) || ReferenceEquals(focused, SizeLockButton))
            return;

        CommitSizeEdit(AxisFor(sender));
    }

    /// <summary>输入框 → 提交轴：0 宽 / 1 高 / 2 X / 3 Y / 4 圆角。</summary>
    private int AxisFor(object? sender)
    {
        if (ReferenceEquals(sender, PosXBox)) return 2;
        if (ReferenceEquals(sender, PosYBox)) return 3;
        if (ReferenceEquals(sender, RadiusBox)) return 4;
        if (ReferenceEquals(sender, HeightBox)) return 1;
        return 0;
    }

    /// <summary>当前生效的宽高比：优先用预设，否则用选区自身的比例。</summary>
    private double EffectiveAspect() =>
        _ratioPreset > 0
            ? _ratioPreset
            : _selection.Height > 0 ? _selection.Width / _selection.Height : 0;

    /// <summary>把输入框里的数值应用到选区：左上角不动，超出窗口就钳制。</summary>
    internal void CommitSizeEdit(int axis)
    {
        if (_sizeEditGuard || !_hasSelection)
            return;

        // 圆角不是几何，单独处理
        if (axis == 4)
        {
            if (int.TryParse(RadiusBox.Text?.Trim(), out var radius) && radius >= 0)
            {
                _cornerRadius = Math.Min(radius, 128);
                if (_hasSelection)
                    UpdateSelectionVisuals();
                SetSelected(CornerRadiusButton, _cornerRadius > 0);
            }
            UpdateSizeBadge(_selection);
            return;
        }

        var unit = _sizeInPixels ? 1 / _scaling : 1;

        // X / Y：整体平移，尺寸不变
        if (axis is 2 or 3)
        {
            var text = axis == 2 ? PosXBox.Text : PosYBox.Text;
            if (!TryParsePosition(text, out var value))
            {
                UpdateSizeBadge(_selection);
                return;
            }

            var pos = value * unit;
            var x = axis == 2
                ? Math.Clamp(pos, 0, Math.Max(0, Width - _selection.Width))
                : _selection.X;
            var y = axis == 3
                ? Math.Clamp(pos, 0, Math.Max(0, Height - _selection.Height))
                : _selection.Y;
            _selection = new Rect(x, y, _selection.Width, _selection.Height);
            UpdateSelectionVisuals();
            RebuildNotePreview();
            return;
        }

        if (!TryParseSize(WidthBox.Text, out var width) || !TryParseSize(HeightBox.Text, out var height))
        {
            UpdateSizeBadge(_selection);
            return;
        }

        // 输入单位跟当前显示单位一致
        var w = width * unit;
        var h = height * unit;

        // 锁定比例：以最后编辑的那个框为准
        var aspect = EffectiveAspect();
        if (_aspectLocked && aspect > 0)
        {
            if (axis == 0)
                h = w / aspect;
            else
                w = h * aspect;
        }

        // 最小 8 DIP，且不能越过窗口右下边
        w = Math.Clamp(w, 8, Math.Max(8, Width - _selection.X));
        h = Math.Clamp(h, 8, Math.Max(8, Height - _selection.Y));

        _selection = new Rect(_selection.X, _selection.Y, w, h);
        UpdateSelectionVisuals();
        RebuildNotePreview();
    }

    private static bool TryParseSize(string? text, out double value) =>
        double.TryParse(
            text?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) && value > 0;

    private static bool TryParsePosition(string? text, out double value) =>
        double.TryParse(
            text?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) && value >= 0;

    // ---------- 宽高比预设 ----------

    private void OnRatioFreeMenu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(0, "自由");
    private void OnRatio1x1Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(1.0, "1:1");
    private void OnRatio3x2Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(3.0 / 2.0, "3:2");
    private void OnRatio4x3Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(4.0 / 3.0, "4:3");
    private void OnRatio16x9Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(16.0 / 9.0, "16:9");
    private void OnRatio2x3Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(2.0 / 3.0, "2:3");
    private void OnRatio3x4Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(3.0 / 4.0, "3:4");
    private void OnRatio9x16Menu(object? sender, RoutedEventArgs e) => ApplyRatioPreset(9.0 / 16.0, "9:16");

    /// <summary>
    /// 应用比例预设：ratio = 0 表示自由。
    /// 选预设会顺带打开比例锁（和参考的行为一致），并把当前选区改成该比例。
    /// </summary>
    internal void ApplyRatioPreset(double ratio, string label)
    {
        _ratioPreset = ratio;
        RatioButton.Content = label;

        if (ratio <= 0)
        {
            UpdateSizeBadge(_selection);
            return;
        }

        SetAspectLock(true);

        if (!_hasSelection)
            return;

        // 以当前宽度为准重算高度；越界就退回高度反算宽度
        var w = _selection.Width;
        var h = w / ratio;
        if (_selection.Y + h > Height)
        {
            h = Math.Max(8, Height - _selection.Y);
            w = h * ratio;
        }

        w = Math.Max(8, Math.Min(w, Width - _selection.X));
        _selection = new Rect(_selection.X, _selection.Y, w, h);
        UpdateSelectionVisuals();
        RebuildNotePreview();
    }

    /// <summary>开关比例锁（菜单和锁图标共用）。</summary>
    private void SetAspectLock(bool locked)
    {
        _aspectLocked = locked;
        SizeLockButton.IsChecked = locked;
        LockOpenIcon.IsVisible = !locked;
        LockClosedIcon.IsVisible = locked;
        ToolTip.SetTip(SizeLockButton, locked ? "已锁定宽高比" : "锁定宽高比");
    }

    internal void ToggleSizeUnit()
    {
        _sizeInPixels = !_sizeInPixels;
        UpdateSizeBadge(_selection);
    }

    // ---------- 样式模板（三槽） ----------

    /// <summary>三槽样式模板；由宿主在窗口显示前灌入，关闭后读回持久化。</summary>
    public AnnotationPreset?[] Presets { get; } = new AnnotationPreset?[3];

    /// <summary>当前标注（测试和导出共用，顺序就是叠放顺序）。</summary>
    internal IReadOnlyList<IAnnotation> Notes => _notes.Items;

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

        if (_hasSelection)
            UpdateSelectionVisuals();
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
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            ShowToolLayoutMenu();
            e.Handled = true;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _draggingToolbar = true;
        _toolbarManual = true;
        _toolbarDragStart = e.GetPosition(this);

        MeasureActionBar();
        _toolbarStartPosition = new Point(
            Canvas.GetLeft(ActionBar),
            Canvas.GetTop(ActionBar));
        e.Handled = true;
    }

    private void MoveToolbar(Point point)
    {
        var size = ActionBar.Bounds.Width > 0 ? ActionBar.Bounds.Size : ActionBar.DesiredSize;
        var screen = ScreenDipFor(_selection) ?? new Rect(0, 0, Width, Height);

        var left = Math.Clamp(
            _toolbarStartPosition.X + (point.X - _toolbarDragStart.X),
            screen.X + 8,
            Math.Max(screen.X + 8, screen.Right - size.Width - 8));
        var top = Math.Clamp(
            _toolbarStartPosition.Y + (point.Y - _toolbarDragStart.Y),
            screen.Y + 8,
            Math.Max(screen.Y + 8, screen.Bottom - size.Height - 8));

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, top);
    }

    private void ShowActionBar(Rect selection)
    {
        ActionBar.IsVisible = true;
        HintBar.IsVisible = false;
        CrosshairLayer.IsVisible = false;
        PositionActionBar(selection);
    }

    /// <summary>
    /// 工具条位置：对齐选区右下角，放得下就放下方，放不下翻到上方，
    /// 最后横向/纵向钳制在窗口内（参考 snow-shot 的 anchoredToolbarPlacement）。
    /// 用实测尺寸而不是估算宽度——按钮数量一变估算就失效。
    /// </summary>
    private bool _compactToolbar;

    /// <summary>0 = 完整，1 = 低频动作进更多，2 = 连 OCR/翻译动作也收起。</summary>
    private int _toolbarDensity;

    private bool _densityApplied;
    private bool _ocrActionsAvailable;
    private bool _translateAvailable;

    /// <summary>完整动作状态下的主条宽度；按钮可见性变化时置 NaN 重测。</summary>
    private double _fullMainBarWidth = double.NaN;

    /// <summary>一级压缩后的主条宽度（OCR/翻译仍在条上）。</summary>
    private double _compactMainBarWidth = double.NaN;

    /// <summary>
    /// 窗口窄于这个宽度（DIP）就把低频动作收起来。
    /// 工具组折叠之后工具条实测 678 宽，加两侧各 8 的边距需要 694，阈值定在 700。
    /// </summary>
    internal static bool ShouldCompact(double width) => width > 0 && width < 700;

    /// <summary>
    /// 窄窗口把低频动作收进「更多」。
    /// 学 snow-shot 的 layout compaction，只是规则更简单（按窗口宽度，而不是逐项量宽）。
    /// </summary>
    private void ApplyToolbarCompaction()
    {
        var available = ToolbarAvailableWidth;

        // 完整/一级压缩宽度只在按钮可见性变化时重测，避免拖动选区时每帧切换可见性
        if (double.IsNaN(_fullMainBarWidth))
        {
            SetToolbarDensity(0);
            MainBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _fullMainBarWidth = MainBar.DesiredSize.Width;

            SetToolbarDensity(1);
            MainBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _compactMainBarWidth = MainBar.DesiredSize.Width;
        }

        var level = _fullMainBarWidth <= available
            ? 0
            : _compactMainBarWidth <= available
                ? 1
                : 2;

        SetToolbarDensity(level);
    }

    internal void SetToolbarCompaction(bool compact) => SetToolbarDensity(compact ? 1 : 0);

    internal void SetToolbarDensity(int level)
    {
        level = Math.Clamp(level, 0, 2);

        if (level == _toolbarDensity && _densityApplied)
            return;

        _toolbarDensity = level;
        _compactToolbar = level > 0;
        _densityApplied = true;
        ApplyDensityVisibility();
    }

    private void ApplyDensityVisibility()
    {
        var compact = _toolbarDensity > 0;

        // ✓ 是主操作，任何宽度都保留；低频动作收进「更多」
        SaveButton.IsVisible = !compact;
        PinButton.IsVisible = !compact;
        ScrollButton.IsVisible = !compact;
        CornerRadiusButton.IsVisible = !compact;
        MoreButton.IsVisible = compact;

        // 二级压缩：收起 OCR 辅助动作和状态文字；翻译是主功能，保留
        TranslateButton.IsVisible = _translateAvailable;
        CopyAllButton.IsVisible = _ocrActionsAvailable && _toolbarDensity < 2;
        OcrBoxButton.IsVisible = _ocrActionsAvailable && _toolbarDensity < 2;

        if (_toolbarDensity >= 2)
            OcrStatus.IsVisible = false;
    }

    private void InvalidateToolbarMetrics()
    {
        _fullMainBarWidth = double.NaN;
        _compactMainBarWidth = double.NaN;
    }

    private void PositionActionBar(Rect selection)
    {
        if (!ActionBar.IsVisible)
            return;

        ApplyToolbarCompaction();

        // 手动拖开后，本次「选区不变」期间保持手动位置；
        // 一旦选区变动（ApplyCreate/Move/Resize）就复位，重新贴回选区
        if (_toolbarManual)
        {
            if (!_toolbarPlacementApplied)
                ApplyToolbarFraction(selection);

            return;
        }

        MeasureActionBar();
        var size = ActionBar.DesiredSize;

        var badge = ComputeSizeBadgePlacement(selection);
        var bounds = ScreenDipFor(selection) ?? new Rect(0, 0, Width, Height);
        var position = ComputeToolbarPosition(
            selection,
            size,
            bounds,
            new Rect(badge.Position, badge.Size));

        Canvas.SetLeft(ActionBar, position.X);
        Canvas.SetTop(ActionBar, position.Y);
        _toolbarPlacementApplied = true;
    }

    /// <summary>用户拖过工具条后，把位置记成当前显示器的 0–1 比例。</summary>
    private void RememberToolbarPlacement()
    {
        var size = ActionBar.Bounds.Width > 0 ? ActionBar.Bounds.Size : ActionBar.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var left = Canvas.GetLeft(ActionBar);
        var top = Canvas.GetTop(ActionBar);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        var screen = ScreenDipFor(_selection) ?? new Rect(0, 0, Width, Height);
        _toolbarFraction = RememberToolbarFraction(
            screen, size, new Point(left, top));
        _toolbarPlacementApplied = true;
    }

    private void ApplyToolbarFraction(Rect selection)
    {
        _toolbarPlacementApplied = true;

        if (_toolbarFraction is not { } fraction)
            return;

        MeasureActionBar();
        var size = ActionBar.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            _toolbarPlacementApplied = false;
            return;
        }

        var screen = ScreenDipFor(selection) ?? new Rect(0, 0, Width, Height);
        var position = RestoreToolbarPosition(screen, size, fraction);
        Canvas.SetLeft(ActionBar, position.X);
        Canvas.SetTop(ActionBar, position.Y);
    }

    internal static Point RememberToolbarFraction(Rect screen, Size toolbar, Point position)
    {
        var rangeX = Math.Max(1, screen.Width - toolbar.Width);
        var rangeY = Math.Max(1, screen.Height - toolbar.Height);

        return new Point(
            Math.Clamp((position.X - screen.X) / rangeX, 0, 1),
            Math.Clamp((position.Y - screen.Y) / rangeY, 0, 1));
    }

    internal static Point RestoreToolbarPosition(Rect screen, Size toolbar, Point fraction)
    {
        var rangeX = Math.Max(0, screen.Width - toolbar.Width);
        var rangeY = Math.Max(0, screen.Height - toolbar.Height);

        return new Point(
            screen.X + Math.Clamp(fraction.X, 0, 1) * rangeX,
            screen.Y + Math.Clamp(fraction.Y, 0, 1) * rangeY);
    }

    /// <summary>工具条可用宽度：留 8px 边距，属性栏按这个宽度折行。</summary>
    private double ToolbarAvailableWidth
    {
        get
        {
            var width = Width > 0 ? Width : 800;
            return Math.Max(240, width - 16);
        }
    }

    /// <summary>约束宽度后再测量，属性栏才能按整组换行。</summary>
    private void MeasureActionBar()
    {
        var available = ToolbarAvailableWidth;
        ActionBar.MaxWidth = available;
        ActionBar.Measure(new Size(available, double.PositiveInfinity));
    }

    /// <summary>布局完成后再按实际 Bounds 校正一次居中，避免用到上一帧的尺寸。</summary>
    private void OnActionBarLayoutUpdated(object? sender, EventArgs e)
    {
        if (!ActionBar.IsVisible || _toolbarManual)
            return;

        var size = ActionBar.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var badge = ComputeSizeBadgePlacement(_selection);
        var screen = ScreenDipFor(_selection) ?? new Rect(0, 0, Width, Height);
        var position = ComputeToolbarPosition(
            _selection,
            size,
            screen,
            new Rect(badge.Position, badge.Size));
        var currentLeft = Canvas.GetLeft(ActionBar);
        var currentTop = Canvas.GetTop(ActionBar);

        if (Math.Abs(currentLeft - position.X) < 0.5 && Math.Abs(currentTop - position.Y) < 0.5)
            return;

        Canvas.SetLeft(ActionBar, position.X);
        Canvas.SetTop(ActionBar, position.Y);
    }

    internal static Point ComputeToolbarPosition(
        Rect selection,
        Size toolbar,
        Size bounds,
        Rect? avoid = null,
        double gap = 8,
        double margin = 8) =>
        ComputeToolbarPosition(
            selection,
            toolbar,
            new Rect(0, 0, bounds.Width, bounds.Height),
            avoid,
            gap,
            margin);

    internal static Point ComputeToolbarPosition(
        Rect selection,
        Size toolbar,
        Rect bounds,
        Rect? avoid = null,
        double gap = 8,
        double margin = 8)
    {
        var minX = bounds.X + margin;
        var maxX = Math.Max(minX, bounds.Right - toolbar.Width - margin);
        var minY = bounds.Y + margin;
        var maxY = Math.Max(minY, bounds.Bottom - toolbar.Height - margin);

        // 锚定在选区下方并水平居中
        var clampedX = Math.Clamp(selection.Center.X - toolbar.Width / 2, minX, maxX);

        bool Fits(Point p)
        {
            if (p.X < minX || p.Y < minY || p.X > maxX || p.Y > maxY)
                return false;

            if (avoid is { } rect)
                return !new Rect(p, toolbar).Intersects(rect);

            return true;
        }

        var below = new Point(clampedX, selection.Bottom + gap);
        if (Fits(below))
            return below;

        var above = new Point(clampedX, selection.Top - toolbar.Height - gap);
        if (Fits(above))
            return above;

        if (avoid is { } badge && badge.Width > 0 && badge.Height > 0)
        {
            // 上方被尺寸浮条挡住：叠到浮条上方
            var aboveBadge = new Point(clampedX, badge.Top - toolbar.Height - gap);
            if (Fits(aboveBadge))
                return aboveBadge;

            // 再试选区左右两侧
            var sideY = Math.Clamp(selection.Center.Y - toolbar.Height / 2, minY, maxY);

            var rightSide = new Point(selection.Right + gap, sideY);
            if (Fits(rightSide))
                return rightSide;

            var leftSide = new Point(selection.Left - toolbar.Width - gap, sideY);
            if (Fits(leftSide))
                return leftSide;
        }

        // 上下都放不下：下方优先，整体钳制，保持确定性
        return new Point(clampedX, Math.Clamp(below.Y, minY, maxY));
    }

    /// <summary>结果卡片：优先选区右侧，其次左侧，最后钳制到窗口内。</summary>
    internal static Point ComputeCardPosition(
        Rect selection,
        Size card,
        Size bounds,
        double gap = 12,
        double margin = 8) =>
        ComputeCardPosition(
            selection,
            card,
            new Rect(0, 0, bounds.Width, bounds.Height),
            gap,
            margin);

    internal static Point ComputeCardPosition(
        Rect selection,
        Size card,
        Rect bounds,
        double gap = 12,
        double margin = 8)
    {
        var left = selection.Right + gap;

        if (left + card.Width > bounds.Right - margin)
            left = selection.X - card.Width - gap;

        left = Math.Clamp(
            left,
            bounds.X + margin,
            Math.Max(bounds.X + margin, bounds.Right - card.Width - margin));

        var top = Math.Clamp(
            selection.Y,
            bounds.Y + margin,
            Math.Max(bounds.Y + margin, bounds.Bottom - card.Height - margin));

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

    private bool IsPointInCustomColorPanel(Point point)
    {
        if (!CustomColorPanel.IsVisible)
            return false;

        if (CustomColorPanel.TranslatePoint(new Point(0, 0), this) is not { } origin)
            return false;

        return new Rect(origin, CustomColorPanel.Bounds.Size).Contains(point);
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
        _translateTarget = options?.TranslateTarget;
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

        var screen = ScreenDipFor(_selection) ?? new Rect(0, 0, Width, Height);
        var position = ComputeCardPosition(_selection, size, screen);
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
        _selectedOcrBoxes.Clear();
        _ocrLines.Clear();
        _ocrMarks.Clear();
        ClearOcrCharSelection();
        HideOcrMarkMenu();
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

        // OCR 阶段不会再探测控件框
        _uiProbe?.ReleaseTrees();

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

            // 字符级选择和点击都在 OcrLayer 上统一处理，行框只负责外观
            var dipLine = new OcrTextLine(
                line.Text,
                line.X / _scaling,
                line.Y / _scaling,
                line.Width / _scaling,
                line.Height / _scaling,
                line.Translation);
            _ocrLines.Add((dipLine, OcrTextLayout.Split(dipLine)));

            OcrLayer.Children.Add(box);
            _ocrBoxes.Add(box);
            Canvas.SetLeft(box, line.X / _scaling - OcrLinePadding);
            Canvas.SetTop(box, line.Y / _scaling - OcrLinePadding);
        }

        ToolTip.SetTip(RecognizeButton, "重新识别选区文字（Enter / R）");
        _ocrActionsAvailable = true;
        OcrStatus.IsVisible = true;
        InvalidateToolbarMetrics();
        ApplyDensityVisibility();
        ApplyOcrBoxMode();
        UpdateTranslatedLayer();
    }

    /// <summary>
    /// 有按行译文时生成"译文图"，盖在选区上；没有就什么都不做。
    /// 底图用这次识别时的截图（`Result`），尺寸和导出一致。
    /// </summary>
    private void UpdateTranslatedLayer()
    {
        _translatedPng = null;
        _showOriginalView = false;
        TransLayer.Source = null;
        TransLayer.IsVisible = false;
        TransRow.IsVisible = false;
        TransTargetText.Text = _translateTarget is { Length: > 0 } target
            ? $"译文 · {target}"
            : "译文";

        var lines = _ocrLines
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Line.Translation))
            .Select(entry => new TranslatedImageRenderer.LineBox(
                new Rect(entry.Line.X, entry.Line.Y, entry.Line.Width, entry.Line.Height),
                entry.Line.Text,
                entry.Line.Translation))
            .ToList();

        if (lines.Count == 0 || Result is not { Length: > 0 } cropPng)
            return;

        using var crop = SKBitmap.Decode(cropPng);
        if (crop is null)
            return;

        var png = TranslatedImageRenderer.Render(crop, _scaling, lines);
        if (png is null)
            return;

        _translatedPng = png;

        using var stream = new MemoryStream(png);
        TransLayer.Source = WriteableBitmap.Decode(stream);
        TransLayer.Width = _selection.Width;
        TransLayer.Height = _selection.Height;
        Canvas.SetLeft(TransLayer, _selection.X);
        Canvas.SetTop(TransLayer, _selection.Y);

        ShowOriginalButton.IsVisible = true;
        TransRow.IsVisible = true;
        ApplyTranslationView();
    }

    /// <summary>按当前视图状态同步译文层 / 行框 / 按钮。</summary>
    private void ApplyTranslationView()
    {
        var hasTranslation = _translatedPng is not null;

        TransLayer.IsVisible = hasTranslation && !_showOriginalView;
        OcrLayer.IsVisible = hasTranslation ? _showOriginalView : OcrLayer.IsVisible;

        if (!hasTranslation)
            return;

        SetSelected(ShowOriginalButton, _showOriginalView);
        ShowOriginalText.Text = _showOriginalView ? "显示译文" : "显示原图";
        ToolTip.SetTip(ShowOriginalButton, _showOriginalView ? "显示译文" : "显示原图");
        OcrStatus.Text = _showOriginalView ? "原文视图" : "译文视图 · 点眼睛切回原文";
    }

    /// <summary>退出译文视图：回到识别框，译文图保留但不再显示。</summary>
    private void OnTransCloseClick(object? sender, RoutedEventArgs e)
    {
        _translatedPng = null;
        _showOriginalView = false;
        TransLayer.Source = null;
        TransLayer.IsVisible = false;
        TransRow.IsVisible = false;
        ApplyTranslationView();
    }

    private void OnShowOriginalClick(object? sender, RoutedEventArgs e)
    {
        if (_translatedPng is null)
            return;

        _showOriginalView = !_showOriginalView;
        ApplyTranslationView();
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
        _selectedOcrBoxes.Clear();
        _ocrLines.Clear();
        _ocrMarks.Clear();
        _ocrSelAnchor = null;
        _ocrSelFocus = null;
        _ocrDragging = false;
        _translatedPng = null;
        _showOriginalView = false;
        TransLayer.Source = null;
        TransLayer.IsVisible = false;
        TransRow.IsVisible = false;
        HideOcrMarkMenu();
        _hoveredOcrBox = null;
        OcrLayer.IsVisible = false;
        _ocrActionsAvailable = false;
        CopyAllButton.IsVisible = false;
        OcrBoxButton.IsVisible = false;
        OcrStatus.IsVisible = false;
        InvalidateToolbarMetrics();
        ToolTip.SetTip(RecognizeButton, "识别选区文字（Enter / R）");
        HideResultCard();        _ocrText = null;
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
        // 图标按钮：状态只体现在 tooltip 和行框可见性上
        ToolTip.SetTip(OcrBoxButton, $"识别框：{OcrBoxModes.Label(_ocrBoxMode)}（B）");
        OcrBoxButton.IsVisible = _ocrActionsAvailable
            && _toolbarDensity < 2
            && OcrLayer.IsVisible;
        OcrLayer.IsHitTestVisible = _ocrBoxMode != OcrBoxMode.Off;
        _hoveredOcrBox = null;

        foreach (var box in _ocrBoxes)
        {
            box.IsVisible = _ocrBoxMode == OcrBoxMode.All;
            ApplyOcrBoxVisual(box, hovered: false);
        }

        if (OcrLayer.IsVisible)
            UpdateOcrStatus();
    }

    private void UpdateOcrStatus()
    {
        var hint = _ocrBoxMode switch
        {
            OcrBoxMode.All => "点行选中 · 双击复制",
            OcrBoxMode.Hover => "悬停行可点",
            _ => "行框已关闭"
        };

        var selected = _selectedOcrBoxes.Count;
        OcrStatus.Text = selected > 0
            ? $"{_ocrBoxes.Count} 行 · 已选 {selected} · Ctrl+C 复制"
            : $"{_ocrBoxes.Count} 行 · {hint}";
    }

    private void OnOcrLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(OcrLayer);

        // 拖选优先：拖动中不参与悬停高亮
        UpdateOcrSelectionDrag(point);

        if (_ocrBoxMode != OcrBoxMode.Hover || _ocrBoxes.Count == 0)
            return;
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

    private void CopyAllText() => CopyOcrText();

    /// <summary>
    /// 有选中就复制选中的行，没有就复制全文。
    /// 选中行按阅读顺序合并（先按 Y 聚行，行内按 X 排）。
    /// </summary>
    internal void CopyOcrText()    {
        if (_selectedOcrBoxes.Count > 0)
        {
            var text = MergeOcrText(_selectedOcrBoxes.Select(box => (
                (box.Tag as string) ?? string.Empty,
                new Rect(Canvas.GetLeft(box), Canvas.GetTop(box), box.Width, box.Height))));

            if (string.IsNullOrWhiteSpace(text))
                return;

            CopyText?.Invoke(text);
            OcrStatus.Text = $"已复制选中 {_selectedOcrBoxes.Count} 行 · {text.Length} 字";
            return;
        }

        if (string.IsNullOrEmpty(_ocrText))
            return;

        CopyText?.Invoke(_ocrText);
        OcrStatus.Text = $"已复制全文 · {_ocrText.Length} 字";
    }

    /// <summary>把若干行框按阅读顺序合并成文本；同一行内按 X 排，行间换行。</summary>
    internal static string MergeOcrText(IEnumerable<(string Text, Rect Bounds)> boxes)
    {
        var items = boxes
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .OrderBy(item => item.Bounds.Y)
            .ThenBy(item => item.Bounds.X)
            .ToList();

        if (items.Count == 0)
            return string.Empty;

        var lines = new List<List<(string Text, Rect Bounds)>>();
        foreach (var item in items)
        {
            var line = lines.Count > 0 ? lines[^1] : null;
            var tolerance = Math.Max(4, item.Bounds.Height / 2);

            if (line is not null && Math.Abs(item.Bounds.Y - line[0].Bounds.Y) <= tolerance)
                line.Add(item);
            else
                lines.Add([item]);
        }

        return string.Join(
            "\n",
            lines.Select(line => JoinFragments(line
                .OrderBy(item => item.Bounds.X)
                .Select(item => item.Text.Trim())
                .ToList())));
    }

    /// <summary>同一行内的碎片拼接：两侧都是西文才补空格，中文之间不加。</summary>
    private static string JoinFragments(IReadOnlyList<string> parts)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
                continue;

            if (builder.Length > 0 && NeedsSpace(builder[^1], part[0]))
                builder.Append(' ');

            builder.Append(part);
        }

        return builder.ToString();
    }

    private static bool NeedsSpace(char left, char right) => left < 0x2E80 && right < 0x2E80;

    private void OnOcrLineEntered(object? sender, PointerEventArgs e)
    {
        // 悬停只加亮边框，中间保持原图可见
        if (sender is Rectangle box)
            ApplyOcrBoxVisual(box, hovered: true);
    }

    private void OnOcrLineExited(object? sender, PointerEventArgs e)
    {
        if (sender is Rectangle box)
            ApplyOcrBoxVisual(box, hovered: false);
    }

    // ---------- OCR 字符级选择 ----------

    private void OnOcrLayerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(OcrLayer);
        var line = OcrLineAt(point);

        if (line < 0)
        {
            ClearOcrCharSelection();
            HideOcrMarkMenu();
            return;
        }

        _ocrPressPoint = point;
        _ocrPressClickCount = e.ClickCount;
        _ocrDragging = false;
        var index = OcrTextLayout.IndexAt(_ocrLines[line].Chars, point);
        _ocrSelAnchor = (line, index);
        _ocrSelFocus = (line, index);
        HideOcrMarkMenu();
        e.Handled = true;
    }

    private void OnOcrLayerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_ocrSelAnchor is not { } anchor)
            return;

        e.Handled = true;

        // 没有拖动：单击切换整行选中，双击复制这一行
        if (!_ocrDragging)
        {
            if (anchor.Line >= 0 && anchor.Line < _ocrBoxes.Count)
            {
                if (_ocrPressClickCount >= 2)
                {
                    var text = _ocrLines[anchor.Line].Line.Text;
                    CopyText?.Invoke(text);
                    OcrStatus.Text = $"已复制：{Shorten(text)}";
                }
                else
                {
                    ToggleOcrBoxSelection(_ocrBoxes[anchor.Line]);
                }
            }

            ClearOcrCharSelection();
            return;
        }

        if (GetOcrSelectionRanges().Count == 0)
        {
            ClearOcrCharSelection();
            return;
        }

        ShowOcrMarkMenu();
    }

    /// <summary>拖动中更新焦点；超过 4 DIP 才算拖选，避免和单击抢。</summary>
    private void UpdateOcrSelectionDrag(Point point)
    {
        if (_ocrSelAnchor is not { } anchor)
            return;

        if (!_ocrDragging
            && Math.Abs(point.X - _ocrPressPoint.X) + Math.Abs(point.Y - _ocrPressPoint.Y) < 4)
        {
            return;
        }

        _ocrDragging = true;

        var line = OcrLineAt(point);
        if (line < 0)
            return;

        _ocrSelFocus = (line, OcrTextLayout.IndexAt(_ocrLines[line].Chars, point));
        RebuildOcrSelectionVisual();
    }

    private int OcrLineAt(Point point)
    {
        for (var i = 0; i < _ocrLines.Count; i++)
        {
            var line = _ocrLines[i].Line;
            var rect = new Rect(line.X - 4, line.Y - 4, line.Width + 8, line.Height + 8);

            if (rect.Contains(point))
                return i;
        }

        return -1;
    }

    /// <summary>把 anchor/focus 归一成「每行一段」的字符区间。</summary>
    internal IReadOnlyList<(int Line, int Start, int Length)> GetOcrSelectionRanges()
    {
        var result = new List<(int Line, int Start, int Length)>();

        if (_ocrSelAnchor is not { } a || _ocrSelFocus is not { } f)
            return result;

        if (a.Line == f.Line && a.Char == f.Char)
            return result;

        if (a.Line > f.Line || (a.Line == f.Line && a.Char > f.Char))
            (a, f) = (f, a);

        for (var line = a.Line; line <= f.Line; line++)
        {
            if (line < 0 || line >= _ocrLines.Count)
                continue;

            var count = _ocrLines[line].Chars.Count;
            var start = line == a.Line ? a.Char : 0;
            var end = line == f.Line ? f.Char : count;

            start = Math.Clamp(start, 0, count);
            end = Math.Clamp(end, 0, count);

            if (end > start)
                result.Add((line, start, end - start));
        }

        return result;
    }

    /// <summary>选区文字：行内取子串，行间换行。</summary>
    internal string SelectedOcrText()
    {
        var ranges = GetOcrSelectionRanges();
        if (ranges.Count == 0)
            return string.Empty;

        return string.Join("\n", ranges.Select(range =>
            OcrTextLayout.TextForRange(_ocrLines[range.Line].Line.Text, range.Start, range.Length)));
    }

    /// <summary>测试与将来的键盘选择用：直接设定字符区间。</summary>
    internal void SetOcrSelection(int line, int start, int end)
    {
        _ocrSelAnchor = (line, start);
        _ocrSelFocus = (line, end);
        _ocrDragging = true;
        RebuildOcrSelectionVisual();
    }

    /// <summary>已打上的文字标记数量。</summary>
    internal int OcrMarkCount => _ocrMarks.Count;

    /// <summary>把选区的字符框画成半透明底（和行框的选中态区分开）。</summary>
    private void RebuildOcrSelectionVisual()    {
        RemoveOcrChildren("ocr-selection");

        foreach (var (line, start, length) in GetOcrSelectionRanges())
        {
            foreach (var box in OcrTextLayout.BoxesForRange(_ocrLines[line].Chars, start, length))
            {
                var highlight = new Rectangle
                {
                    Width = box.Width,
                    Height = box.Height,
                    Fill = OcrCharSelectionFill,
                    IsHitTestVisible = false,
                    Tag = "ocr-selection"
                };

                Canvas.SetLeft(highlight, box.X);
                Canvas.SetTop(highlight, box.Y);
                OcrLayer.Children.Add(highlight);
            }
        }
    }

    private void ClearOcrCharSelection()
    {
        _ocrSelAnchor = null;
        _ocrSelFocus = null;
        _ocrDragging = false;
        RebuildOcrSelectionVisual();
    }

    /// <summary>切换一条文字标记：同样类型 + 同样框再点一次就取消。</summary>
    internal void ApplyOcrMark(TextMarkKind kind)
    {
        var ranges = GetOcrSelectionRanges();
        if (ranges.Count == 0)
            return;

        var boxes = ranges
            .SelectMany(range => OcrTextLayout.BoxesForRange(
                _ocrLines[range.Line].Chars, range.Start, range.Length))
            .ToList();

        if (boxes.Count == 0)
            return;

        var existing = _ocrMarks.FindIndex(mark => mark.Kind == kind && SameBoxes(mark.Boxes, boxes));

        if (existing >= 0)
            _ocrMarks.RemoveAt(existing);
        else
            _ocrMarks.Add(new OcrTextMark(kind, boxes, OcrTextMarks.ColorFor(kind), 0));

        RebuildOcrMarksVisual();
    }

    private static bool SameBoxes(IReadOnlyList<Rect> left, IReadOnlyList<Rect> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (Math.Abs(left[i].X - right[i].X) > 0.5 ||
                Math.Abs(left[i].Y - right[i].Y) > 0.5 ||
                Math.Abs(left[i].Width - right[i].Width) > 0.5 ||
                Math.Abs(left[i].Height - right[i].Height) > 0.5)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把标记画到 OCR 层（预览）；导出时同一批标记会画进 PNG。</summary>
    private void RebuildOcrMarksVisual()
    {
        RemoveOcrChildren("ocr-mark");

        foreach (var mark in _ocrMarks)
        {
            var brush = new SolidColorBrush(Color.FromUInt32(mark.Color));

            foreach (var box in mark.Boxes)
            {
                if (mark.Kind == TextMarkKind.Highlight)
                {
                    var fill = new Rectangle
                    {
                        Width = box.Width,
                        Height = box.Height,
                        Fill = brush,
                        IsHitTestVisible = false,
                        Tag = "ocr-mark"
                    };

                    Canvas.SetLeft(fill, box.X);
                    Canvas.SetTop(fill, box.Y);
                    OcrLayer.Children.Add(fill);
                    continue;
                }

                var width = Math.Clamp(box.Height / 14, 1.5, 3);
                var y = mark.Kind == TextMarkKind.Strike ? box.Center.Y : box.Bottom - 1;
                var line = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = width,
                    IsHitTestVisible = false,
                    Tag = "ocr-mark"
                };

                if (mark.Kind == TextMarkKind.Wave)
                {
                    for (var x = box.X; x <= box.Right; x += 1)
                        line.Points.Add(new Point(x, y + Math.Sin((x - box.X) * 0.7) * width));
                }
                else
                {
                    line.Points.Add(new Point(box.X, y));
                    line.Points.Add(new Point(box.Right, y));
                }

                OcrLayer.Children.Add(line);
            }
        }
    }

    private void RemoveOcrChildren(string tag)
    {
        for (var i = OcrLayer.Children.Count - 1; i >= 0; i--)
        {
            if (OcrLayer.Children[i] is Control { Tag: string value } && value == tag)
                OcrLayer.Children.RemoveAt(i);
        }
    }

    /// <summary>在选区上方/下方摆浮动菜单。</summary>
    private void ShowOcrMarkMenu()
    {
        var ranges = GetOcrSelectionRanges();
        if (ranges.Count == 0)
        {
            HideOcrMarkMenu();
            return;
        }

        var boxes = ranges
            .SelectMany(range => OcrTextLayout.BoxesForRange(
                _ocrLines[range.Line].Chars, range.Start, range.Length))
            .ToList();

        var bounds = boxes.Skip(1).Aggregate(boxes[0], (current, box) => current.Union(box));

        OcrMarkMenu.IsVisible = true;
        OcrMarkMenu.Measure(Size.Infinity);

        var size = OcrMarkMenu.DesiredSize;
        var origin = OcrLayer.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        var left = origin.X + bounds.Center.X - size.Width / 2;
        var top = origin.Y + bounds.Bottom + 10;

        if (top + size.Height > Height - 8)
            top = origin.Y + bounds.Top - size.Height - 10;

        Canvas.SetLeft(OcrMarkMenu, Math.Clamp(left, 8, Math.Max(8, Width - size.Width - 8)));
        Canvas.SetTop(OcrMarkMenu, Math.Max(8, top));
    }

    private void HideOcrMarkMenu() => OcrMarkMenu.IsVisible = false;

    private void OnOcrMarkCopyClick(object? sender, RoutedEventArgs e)
    {
        var text = SelectedOcrText();
        if (string.IsNullOrEmpty(text))
            return;

        CopyText?.Invoke(text);
        OcrStatus.Text = $"已复制选中文字 · {text.Length} 字";
    }

    private void OnOcrHighlightClick(object? sender, RoutedEventArgs e) => ApplyOcrMark(TextMarkKind.Highlight);

    private void OnOcrWaveClick(object? sender, RoutedEventArgs e) => ApplyOcrMark(TextMarkKind.Wave);

    private void OnOcrUnderlineClick(object? sender, RoutedEventArgs e) => ApplyOcrMark(TextMarkKind.Underline);

    private void OnOcrStrikeClick(object? sender, RoutedEventArgs e) => ApplyOcrMark(TextMarkKind.Strike);

    /// <summary>切换一行框的选中状态（点击和测试共用）。</summary>
    internal void ToggleOcrBoxSelection(Rectangle box)
    {
        if (!_selectedOcrBoxes.Remove(box))
            _selectedOcrBoxes.Add(box);

        // 点完先按「未悬停」画：悬停态交给 PointerEntered，避免和选中态撞色
        ApplyOcrBoxVisual(box, hovered: false);
        UpdateOcrSelectionStatus();
    }

    /// <summary>按「是否选中 / 是否悬停」刷新一个行框的三态外观。</summary>
    private void ApplyOcrBoxVisual(Rectangle box, bool hovered)
    {
        if (_selectedOcrBoxes.Contains(box))
        {
            // 选中：琥珀实线 + 淡琥珀底，和工具条选中态一致
            box.Stroke = LineHoverStroke;
            box.StrokeThickness = 2;
            box.Fill = LineSelectedFill;
            return;
        }

        // 常态红框 1px；悬停红框 2px + 更重的底
        box.Stroke = LineStroke;
        box.StrokeThickness = hovered ? 2 : 1;
        box.Fill = hovered ? LineHoverFill : LineFill;
    }

    /// <summary>选中数变化后刷新状态文字和复制按钮的提示。</summary>
    private void UpdateOcrSelectionStatus()
    {
        UpdateOcrStatus();

        var count = _selectedOcrBoxes.Count;
        ToolTip.SetTip(
            CopyAllButton,
            count > 0 ? $"复制选中的 {count} 行（Ctrl+C）" : "复制全部识别文字（Ctrl+C）");
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

        if (key == Key.Escape)
        {
            if (_colorSampling)
            {
                ExitColorSampling();
                e.Handled = true;
                return;
            }

            if (_angleDraft.IsActive || _anglePhase != AnglePhase.None)
            {
                CancelAngleDraft();
                e.Handled = true;
                return;
            }

            // 正在拖选区/拖标注/画一半：先取消这次操作，不关窗口
            if (CancelActiveDrag())
            {
                e.Handled = true;
                return;
            }

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

        var screen = ScreenDipFor(rect) ?? new Rect(0, 0, Width, Height);
        var left = Math.Clamp(
            rect.X,
            screen.X + 4,
            Math.Max(screen.X + 4, screen.Right - size.Width - 4));

        var top = rect.Y - size.Height - gap;
        if (top < screen.Y + 4)
            top = rect.Y + 4;
        top = Math.Clamp(
            top,
            screen.Y + 4,
            Math.Max(screen.Y + 4, screen.Bottom - size.Height - 4));

        return (size, new Point(left, top));
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
