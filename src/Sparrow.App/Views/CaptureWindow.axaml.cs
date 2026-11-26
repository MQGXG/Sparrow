// CaptureWindow 分部类：窗口生命周期、共享字段与构造函数。
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

/// <summary>截图完成后用户选择的动作。</summary>
public enum CaptureAction
{
    None,
    Recognize,
    CopyImage,
    Save,
    Pin,

    /// <summary>进入滚动截图；结果由 ScrollResultWindow 再选择动作。</summary>
    Scroll,

    /// <summary>录制选区；不截图，只把 SelectionRect 交给宿主录制会话。</summary>
    Record
}

/// <summary>
/// 全屏选区窗口。
/// 用鼠标拖出矩形，松开后截取该区域（物理像素），然后显示动作栏。
/// </summary>
public partial class CaptureWindow : Window
{
    private const double OcrLinePadding = 2;
    private const double MinSelectionSize = 4;

    private static readonly bool SelectorDiagnostics = Debugger.IsAttached
        || Environment.GetEnvironmentVariable("SPARROW_SELECTOR_DIAGNOSTICS") == "1";

    private static void SelectorLog(string message)
    {
        if (SelectorDiagnostics)
            Debug.WriteLine($"[Sparrow.Selector] {message}");
    }

    private static string Describe(IReadOnlyList<UiElementCandidate> path) =>
        string.Join(" > ", path.Select(candidate =>
            $"{candidate.ControlType}:{candidate.Bounds.Width}x{candidate.Bounds.Height}"));

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
        NoteRotate,

        /// <summary>拖动序号组合的目标点。</summary>
        NumberTarget
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

    /// <summary>取色时放大倍数更高，像素网格更清楚（参考 snow-shot 的取色器）。</summary>
    private const double ColorPickZoom = 10;

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

    /// <summary>左右对比图（PNG）。</summary>
    private byte[]? _comparePng;

    /// <summary>是否处于左右对比视图。</summary>
    private bool _compareView;

    private (int Line, int Char)? _ocrSelAnchor;
    private (int Line, int Char)? _ocrSelFocus;
    private bool _ocrDragging;
    private int _ocrPressClickCount = 1;
    private Point _ocrPressPoint;

    private Rectangle? _hoveredOcrBox;
    private IReadOnlyList<WindowCandidate> _candidates = [];
    private long _candidatesAt;
    private WindowCandidate? _currentCandidate;
    private WindowCandidate? _candidateAtPress;
    private UiElementCandidate? _uiCandidate;
    private UiElementCandidate? _uiCandidateAtPress;
    private UiElementProbe? _uiProbe;
    private UiElementProbe? _refinementProbe;

    /// <summary>已经做过 IAccessible 激活的窗口：每个窗口只激活一次。</summary>
    private readonly HashSet<IntPtr> _accessibilityActivated = [];

    /// <summary>宿主在遮罩显示前预建好的 UIA 探测实例（浏览器窗口用）。</summary>
    public UiElementProbe? ElementProbe { get; set; }

    private bool UsePrewarmedTree => ElementProbe is not null;
    private readonly ElementSelectionModel _elementSelection = new();
    private IntPtr _captureHandle;
    private int _probeGeneration;
    private Task? _probeTask;
    private IntPtr _probingWindow;
    private PixelPoint _probePoint;
    private bool _refining;
    private MsaaElementProbe? _msaaProbe;
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

    /// <summary>上一次中间译文重绘的时间（TickCount64），用来限帧。</summary>
    private long _lastPartialRender;

    /// <summary>格式 / 标点：只影响复制出去的文本，不改行框与译文落点。</summary>
    private string _ocrFormatting = OcrTextTransform.FormatKeep;

    private string _ocrPunctuation = OcrTextTransform.PunctuationNone;

    /// <summary>翻译引擎：auto / ai / local:{模型Id}；在 OCR 结果条的下拉里选择。</summary>
    private string _translateEngine = "auto";

    private string _translateEngineLabel = "自动（在线优先）";

    /// <summary>OCR 结果条里选中的目标语言；为空时翻译按钮不可用。</summary>
    private string _translateTargetSelection = "";

    private bool _engineAvailable;

    private static readonly (string Value, string Label)[] OcrFormattingOptions =
    [
        (OcrTextTransform.FormatNone, "不处理"),
        (OcrTextTransform.FormatKeep, "保留换行"),
        (OcrTextTransform.FormatRemove, "去掉换行"),
        (OcrTextTransform.FormatSmart, "智能排版")
    ];

    private static readonly (string Value, string Label)[] OcrPunctuationOptions =
    [
        (OcrTextTransform.PunctuationNone, "不处理"),
        (OcrTextTransform.PunctuationHalf, "半角"),
        (OcrTextTransform.PunctuationFull, "全角")
    ];

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
    private uint _noteFillColor;          // 0 = 跟随描边色
    private double _noteRadius;
    private LineDash _noteDash = LineDash.Solid;
    private LineStyle _noteStyle = LineStyle.Neat;
    private double _noteFontSize = TextFontSize;
    private bool _noteBold;
    private bool _noteItalic;
    private string _noteFontFamily = "";
    private TextAlign _noteAlign = TextAlign.Left;
    private uint _textBackground;               // 0 = 无背景
    private uint _textStrokeColor = 0xFF000000;
    private double _textStrokeWidth;            // 0 = 无描边
    private int _mosaicCell = MosaicCell;
    private double _numberSize = NumberSize;
    private NumberFormat _numberFormat = NumberFormat.Arabic;
    private NumberShape _numberShape = NumberShape.SolidCircle;
    private NumberCombo _numberCombo = NumberCombo.Plain;
    private double _numberTextSize = 16;
    private double _numberZoom = 2.5;

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
    private uint _bubbleFillColor;       // 0 = 跟随描边色

    // 工具条拖动：手动位置之后不再跟随选区
    private bool _draggingToolbar;
    private bool _toolbarManual;
    private bool _toolbarPlacementApplied;
    private Point? _toolbarFraction;
    private Point _toolbarDragStart;
    private Point _toolbarStartPosition;

    // 附属行（属性栏 / 译文栏）相对主条的排布方向：翻到选区上方时也要翻上去
    private bool _barAbove;
    private bool _barOrderApplied;

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
    private bool _colorSampling = true;

    /// <summary>
    /// OCR / 翻译 / 滚动截图期间暂停自动取色：
    /// 结果条出现后取色卡片还跟着鼠标会挡住文字（对齐 Snow-shot 的 hideColorPicker）。
    /// </summary>
    private bool _colorProbeSuppressed;

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
        FontFamilyBox.ItemsSource = AnnotationText.Families;
        BuildToolRegistry();
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
        BuildPresetColors();
        BuildOcrTextMenus();

        // 取色卡片与放大镜要压在工具条之上，避免被主条/属性栏盖住
        ColorProbe.SetValue(Panel.ZIndexProperty, 101);
        Magnifier.SetValue(Panel.ZIndexProperty, 100);
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

    /// <summary>窗口元素识别 API：auto / uia / msaa。</summary>
    public string CaptureElementApi { get; set; } = "auto";

    /// <summary>取帧 API：auto / wgc / gdi。</summary>
    public string CaptureFrameApi { get; set; } = "auto";

    /// <summary>普通截图是否画鼠标光标。</summary>
    public bool CaptureShowCursor { get; set; }

    /// <summary>选区边框颜色（#RRGGBB）；同时用于十字辅助线和悬停候选框。</summary>
    public string SelectionBorderColor { get; set; } = "#E5A44C";

    /// <summary>纯标注模式：窗口显示一张现成图片，只保留标注工具条，不进入截图流程。</summary>
    internal bool AnnotationOnly { get; set; }

    /// <summary>标注模式的原图（PNG）。</summary>
    internal byte[]? AnnotationSource { get; set; }

    /// <summary>标注完成后的 PNG；取消或没有标注时为 null。</summary>
    internal byte[]? AnnotationResult { get; private set; }

    private byte[]? _annotationBasePng;
    private Bitmap? _annotationBitmap;
    private double _annotationImageHeight;

    /// <summary>是否还原放大镜全屏颜色滤镜。</summary>
    public bool CaptureRestoreColors { get; set; } = true;

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
    /// <c>partialLines</c> 是翻译过程中的中间结果：截图窗口用它边翻译边画。
    /// </summary>
    public Func<
        byte[],
        CaptureOcrOptions,
        IProgress<IReadOnlyList<OcrTextLine>>?,
        CancellationToken,
        Task<CaptureOcrResult>>? OcrRunner { get; set; }

    /// <summary>
    /// OCR 结果条里的目标语言列表（值 + 显示名），来自 OCR 工具的声明。
    /// 为空时翻译按钮隐藏，避免给出一个点了没反应的入口。
    /// </summary>
    public void ConfigureTranslateTargets(IReadOnlyList<(string Value, string Label)> targets)
    {
        if (TranslateTargetButton.Flyout is not MenuFlyout menu)
        {
            _translateAvailable = false;
            TranslateButton.IsVisible = false;
            TranslateTargetButton.IsVisible = false;
            InvalidateToolbarMetrics();
            return;
        }

        menu.Items.Clear();

        foreach (var (value, label) in targets)
        {
            var item = new MenuItem
            {
                Header = label,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "translate-target",
                IsChecked = value == _translateTargetSelection
            };

            item.Click += async (_, _) =>
            {
                _translateTargetSelection = value;
                TranslateTargetButton.Content = label;
                ToolTip.SetTip(TranslateTargetButton, $"目标语言：{label}");

                foreach (var entry in menu.Items.OfType<MenuItem>())
                    entry.IsChecked = ReferenceEquals(entry, item);

                await RunTranslateAsync(value);
            };

            menu.Items.Add(item);
        }

        // 默认选第一个目标语言，翻译按钮才有明确行为
        if (string.IsNullOrEmpty(_translateTargetSelection) && targets.Count > 0)
        {
            _translateTargetSelection = targets[0].Value;

            if (menu.Items[0] is MenuItem first)
            {
                first.IsChecked = true;

                if (first.Header is string header)
                {
                    TranslateTargetButton.Content = header;
                    ToolTip.SetTip(TranslateTargetButton, $"目标语言：{header}");
                }
            }
        }

        _translateAvailable = targets.Count > 0;
        TranslateActionButton.IsVisible = _translateAvailable;

        // 第二排的翻译只在译文视图用于切换原文/译文；纯 OCR 时主条已有翻译入口
        TranslateButton.IsVisible = _translateAvailable && _translatedPng is not null;
        TranslateTargetButton.IsVisible = _translateAvailable && _translatedPng is not null;
        TranslateOptionsSeparator.IsVisible = _translateAvailable && _translatedPng is not null;
        InvalidateToolbarMetrics();
    }

    /// <summary>
    /// 「翻译引擎」列表（值 + 显示名），来自宿主：在线模型 + 已下载的本地模型。
    /// 直接填充 OCR 结果条上的引擎下拉；没有可选项时按钮隐藏。
    /// </summary>
    public void ConfigureTranslateEngines(IReadOnlyList<(string Value, string Label)> engines)
    {
        if (EngineButton.Flyout is not MenuFlyout menu)
        {
            _engineAvailable = false;
            EngineButton.IsVisible = false;
            InvalidateToolbarMetrics();
            return;
        }

        menu.Items.Clear();

        foreach (var (value, label) in engines)
        {
            var item = new MenuItem
            {
                Header = label,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "translate-engine",
                IsChecked = value == _translateEngine
            };

            item.Click += (_, _) =>
            {
                _translateEngine = value;
                _translateEngineLabel = label;
                UpdateEngineTip();

                foreach (var entry in menu.Items.OfType<MenuItem>())
                    entry.IsChecked = ReferenceEquals(entry, item);
            };

            menu.Items.Add(item);
        }

        _engineAvailable = menu.Items.Count > 0;
        EngineButton.IsVisible = _engineAvailable;

        var current = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => item.IsChecked);

        if (current?.Header is string header)
            _translateEngineLabel = header;

        UpdateEngineTip();
        InvalidateToolbarMetrics();
    }

    private void UpdateEngineTip()
    {
        EngineButton.Content = _translateEngineLabel;
        ToolTip.SetTip(EngineButton, $"翻译引擎：{_translateEngineLabel}");
    }

    /// <summary>构建 OCR 结果条上的「复制格式 / 标点」下拉；它们只影响复制出去的文本。</summary>
    private void BuildOcrTextMenus()
    {
        BuildOptionMenu(FormatButton, OcrFormattingOptions, _ocrFormatting, value =>
        {
            _ocrFormatting = value;
            UpdateOcrTextOptionTips();
        });

        BuildOptionMenu(PunctuationButton, OcrPunctuationOptions, _ocrPunctuation, value =>
        {
            _ocrPunctuation = value;
            UpdateOcrTextOptionTips();
        });

        UpdateOcrTextOptionTips();
    }

    private static void BuildOptionMenu(
        Button button,
        IReadOnlyList<(string Value, string Label)> options,
        string current,
        Action<string> apply)
    {
        if (button.Flyout is not MenuFlyout menu)
            return;

        menu.Items.Clear();
        var group = button.Name ?? "ocr-text-option";

        foreach (var (value, label) in options)
        {
            var item = new MenuItem
            {
                Header = label,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = group,
                IsChecked = value == current
            };

            // 同一个值反复点也只改一次状态；手写勾选，避免依赖 Radio 的自动互斥
            item.Click += (_, _) =>
            {
                apply(value);
                foreach (var entry in menu.Items.OfType<MenuItem>())
                    entry.IsChecked = ReferenceEquals(entry, item);
            };

            menu.Items.Add(item);
        }
    }

    private void UpdateOcrTextOptionTips()
    {
        var format = OcrFormattingOptions.First(option => option.Value == _ocrFormatting).Label;
        var punctuation = OcrPunctuationOptions.First(option => option.Value == _ocrPunctuation).Label;

        ToolTip.SetTip(FormatButton, $"复制格式：{format}");
        ToolTip.SetTip(PunctuationButton, $"标点：{punctuation}");
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

        // 纯标注模式：窗口显示一张现成图片，只保留标注工具条，不进入截图流程。
        if (AnnotationOnly)
        {
            StartAnnotationMode();
            return;
        }
        // 与 Snow-shot 一致：录制中重新截图/选区，遮罩和选区会进入视频。
        // （如果希望录制中截图不污染视频，把下面这行加回来即可。）
        // WindowCaptureExclusion.Exclude(this);

        ScreenCapture.Api = CaptureFrameApi switch
        {
            "gdi" => CaptureApi.Gdi,
            "wgc" => CaptureApi.Wgc,
            _ => CaptureApi.Auto
        };
        ScreenCapture.ShowCursor = CaptureShowCursor;
        ScreenCapture.RestoreColors = CaptureRestoreColors;

        ApplyVirtualScreenBounds();

        _captureHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        SelectorTrace.Write(
            $"open api={CaptureElementApi} handle=0x{_captureHandle.ToInt64():X} " +
            $"cursor={WindowCandidates.GetCursorPosition()} prewarmed={ElementProbe is not null}");
        _candidates = WindowCandidates.Enumerate(_captureHandle);
        _candidatesAt = Environment.TickCount64;
        _uiProbe = ElementProbe ?? new UiElementProbe();
        _msaaProbe = new MsaaElementProbe();

        // 打开遮罩时鼠标可能已经停在目标控件上，不用晃一下才出候选
        ShowCandidateUnderCursor();

        if (FreezeOnOpen)
            _ = FreezeScreenAsync();

        // 宿主在构造后注入上次的样式；这里把按钮状态同步过来
        RefreshNoteStyle();
        ApplySelectionBorderColor();
        BuildPresetMenu();
    }

    /// <summary>
    /// 纯标注模式初始化：窗口铺满屏幕（透明），图片居中显示、工具条可自由拖动，
    /// 不把图片和工具条绑在同一个范围内。
    /// </summary>
    private void StartAnnotationMode()
    {
        if (AnnotationSource is not { Length: > 0 } png)
        {
            Close();
            return;
        }

        _annotationBasePng = png;

        using var stream = new MemoryStream(png);
        _annotationBitmap = new Bitmap(stream);

        // 窗口铺满虚拟屏幕，图片居中；工具条默认放图片下方，之后可以拖到任意位置
        ApplyVirtualScreenBounds();

        ActionBar.IsVisible = true;
        HintBar.IsVisible = false;
        MeasureActionBar();
        var toolbarSize = ActionBar.DesiredSize;

        var screen = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var renderScale = RenderScaling > 0 ? RenderScaling : 1;
        var maxWidth = Math.Max(240, screen.Width / renderScale * 0.8);
        var maxHeight = Math.Max(180, screen.Height / renderScale * 0.8);

        var ratio = Math.Min(
            Math.Min(
                maxWidth / _annotationBitmap.PixelSize.Width,
                maxHeight / _annotationBitmap.PixelSize.Height),
            1.0);

        var imageWidth = Math.Max(160, _annotationBitmap.PixelSize.Width * ratio);
        var imageHeight = Math.Max(120, _annotationBitmap.PixelSize.Height * ratio);

        var imageLeft = Math.Max(8, (Width - imageWidth) / 2);
        var imageTop = Math.Max(8, (Height - imageHeight) / 2);

        FrozenFrame.Source = _annotationBitmap;
        FrozenFrame.IsVisible = true;
        FrozenFrame.Width = imageWidth;
        FrozenFrame.Height = imageHeight;
        Canvas.SetLeft(FrozenFrame, imageLeft);
        Canvas.SetTop(FrozenFrame, imageTop);

        _annotationImageHeight = imageHeight;
        _selection = new Rect(imageLeft, imageTop, imageWidth, imageHeight);
        _hasSelection = true;
        _scaling = imageWidth > 0 ? _annotationBitmap.PixelSize.Width / imageWidth : 1;
        _colorSampling = false;

        HideAnnotationChrome();

        // 工具条先放图片下方；下方放不下就放上方
        var toolbarTop = imageTop + imageHeight + 10;

        if (toolbarTop + toolbarSize.Height > Height - 8)
            toolbarTop = Math.Max(8, imageTop - toolbarSize.Height - 10);

        Canvas.SetLeft(ActionBar, Math.Max(8, (Width - toolbarSize.Width) / 2));
        Canvas.SetTop(ActionBar, toolbarTop);
    }

    /// <summary>标注模式只保留标注工具：隐藏选区、角标、遮罩和截图动作。</summary>
    private void HideAnnotationChrome()
    {
        Selection.IsVisible = false;
        SelectionHalo.IsVisible = false;
        DimLayer.IsVisible = false;
        CrosshairLayer.IsVisible = false;
        CandidateBox.IsVisible = false;
        CandidateBadge.IsVisible = false;
        HandleLayer.IsVisible = false;
        GuideLayer.IsVisible = false;
        NoteHandleLayer.IsVisible = false;
        SizeBadge.IsVisible = false;
        HintBar.IsVisible = false;
        Magnifier.IsVisible = false;
        ColorProbe.IsVisible = false;
        OcrTextBar.IsVisible = false;

        SaveButton.IsVisible = false;
        PinButton.IsVisible = false;
        ScrollButton.IsVisible = false;
        RecordButton.IsVisible = false;
        CornerRadiusButton.IsVisible = false;
        MoreButton.IsVisible = false;

        // 识别 / 翻译保留：标注时也常需要把图上的字提出来
        ToolTip.SetTip(CopyImageButton, "完成标注");
    }

    /// <summary>设置里的选区颜色：选区边框、十字辅助线、悬停候选框统一使用。</summary>
    private void ApplySelectionBorderColor()
    {
        if (!Color.TryParse(SelectionBorderColor, out var color))
            color = Color.Parse("#E5A44C");

        Selection.Stroke = new SolidColorBrush(color);
        CandidateBox.Stroke = new SolidColorBrush(color);

        // 十字线用同色半透明，避免抢过选区边框
        var faint = new SolidColorBrush(Color.FromArgb(0x73, color.R, color.G, color.B));
        CrosshairV.Stroke = faint;
        CrosshairH.Stroke = faint;
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
        // 录制中按 Esc / 关窗：视为取消，丢弃录制并提示。
        if (_recording is not null)
        {
            _recording.StateChanged -= OnRecordingStateChanged;
            _recording.Abandon();
            _recording = null;
            RecordingFinished?.Invoke(null, null, false);
        }

        _regionOverlay?.Close();
        _regionOverlay = null;

        RemoveHitTestHook();

        _probeGeneration++;
        _uiProbe?.Dispose();
        _uiProbe = null;
        _refinementProbe?.Dispose();
        _refinementProbe = null;
        _msaaProbe?.Dispose();
        _msaaProbe = null;
        _ocrCancellation?.Cancel();
        _ocrCancellation?.Dispose();
        _ocrCancellation = null;
        _groupShowTimer?.Stop();

        var releasedBytes = (_frozenPixels?.Length ?? 0)
            + (long)_frozenWidth * _frozenHeight * 4;

        FrozenFrame.Source = null;
        _frozenBitmap?.Dispose();
        _frozenBitmap = null;
        _frozenPixels = null;

        _annotationBitmap?.Dispose();
        _annotationBitmap = null;

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
}
