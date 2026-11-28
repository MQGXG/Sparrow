using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.History;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 工作区：工具列表 + 输入 + 结果 + 参数。
///
/// 与命令面板的区别：工作区的输入**可编辑**，面板的输入取自剪贴板。
/// 两者共用同一个 <see cref="ToolCatalog"/>，插件只加载一次。
/// </summary>
public partial class WorkspaceViewModel : ObservableObject
{
    private readonly ToolCatalog _catalog;
    private readonly IClipboardAccess _clipboard;
    private readonly IAppLog _log;
    private readonly IHistoryStore _history;
    private readonly ToolParameterStore? _parameters;
    private readonly SvgPreviewService _svgPreview;

    public WorkspaceViewModel(
        ToolCatalog catalog,
        IClipboardAccess clipboard,
        IAppLog log,
        IHistoryStore history,
        SettingsViewModel settings,
        ToolParameterStore? parameters = null,
        SvgPreviewService? svgPreview = null,
        ClipboardHistoryStore? clipboardHistory = null,
        ClipboardPasteService? pasteService = null,
        bool loadClipboardOnCreate = true)
    {
        _catalog = catalog;
        _clipboard = clipboard;
        _log = log;
        _history = history;
        _parameters = parameters;
        _svgPreview = svgPreview ?? new SvgPreviewService();
        Settings = settings;

        if (clipboardHistory is not null)
        {
            Clipboard = new ClipboardHistoryViewModel(
                clipboardHistory,
                clipboard,
                pasteService,
                settings,
                refreshOnCreate: loadClipboardOnCreate);
        }
    }

    public SettingsViewModel Settings { get; }

    /// <summary>
    /// 隐藏一段时间后释放可再生数据：剪贴板条目/详情、图片缓存。
    /// 输入图、结果和历史记录保留，避免用户回来时丢正在做的事。
    /// </summary>
    public void ReleaseTransientState()
    {
        Clipboard?.ReleaseTransientState();
        ImageCache.Instance.Clear();
    }

    /// <summary>剪贴板页；测试或未接入监听时为 null。</summary>
    public ClipboardHistoryViewModel? Clipboard { get; }

    public ObservableCollection<ToolItemViewModel> VisibleTools { get; } = [];

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    /// <summary>工具墙统计：当前过滤后的工具数。</summary>
    public int ToolCount => VisibleTools.Count;

    public ObservableCollection<RecentToolViewModel> RecentTools { get; } = [];

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    private List<HistoryEntryViewModel> _allHistory = [];

    /// <summary>工具全集缓存，侧栏搜索在这上面过滤，不重新扫插件。</summary>
    private List<ToolEntry> _allTools = [];

    /// <summary>顶部的工具搜索框：在工具墙上过滤。</summary>
    [ObservableProperty]
    private string _toolQuery = string.Empty;

    /// <summary>侧栏选中的分类；null = 全部。</summary>
    [ObservableProperty]
    private string? _selectedCategory;

    /// <summary>工作区两态：工具墙 / 工具台。</summary>
    [ObservableProperty]
    private bool _isDetailOpen;

    /// <summary>剪贴板页；和工具墙互斥，工具台优先。</summary>
    [ObservableProperty]
    private bool _isClipboardOpen;

    public bool IsToolsSelected => !IsClipboardOpen;

    public bool IsWallVisible => !IsDetailOpen && !IsClipboardOpen;

    public bool IsClipboardVisible => IsClipboardOpen && !IsDetailOpen;

    partial void OnIsClipboardOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(IsWallVisible));
        OnPropertyChanged(nameof(IsClipboardVisible));
        UpdateCategorySelection();
    }

    partial void OnIsDetailOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWallVisible));
        OnPropertyChanged(nameof(IsClipboardVisible));

        // 工具台打开时创建插件视图，返回墙时断开并释放
        if (value)
            RefreshToolView();
        else
            ClearToolView();
    }

    partial void OnToolQueryChanged(string value)
    {
        // 剪贴板页输入搜索词 = 想找工具，直接回工具墙看结果
        if (value.Length > 0)
            IsClipboardOpen = false;

        RefilterTools();
    }

    partial void OnSelectedCategoryChanged(string? value)
    {
        // 选中状态只在 Categories 上，选完必须同步书签条，否则高亮会停在「全部」
        UpdateCategorySelection();
        // 分类过滤列表（和之前一致）；分组只负责视觉分节
        RefilterTools();
    }


    /// <summary>图片结果的规格摘要，用作历史记录与复制提示。</summary>
    public static string SummarizeImage(ImagePayload payload) =>
        $"{payload.Width}×{payload.Height} · {payload.Format.ToString().ToLowerInvariant()} · {payload.ByteSize / 1024.0:F1} KB";

    public void ShowSvgPreview() => ShowSvgSource = false;

    public void ShowSvgSourceView() => ShowSvgSource = true;

    /// <summary>结果区右上角的字数/行数；空结果时为空串。</summary>
    public string ResultSummary => TextMetrics.Summarize(ResultText);

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(ResultSummary));

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isSettingsVisible;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>工具搜索无命中时侧栏显示空态。</summary>
    [ObservableProperty]
    private bool _hasTools;

    /// <summary>当前工具没有参数时，主区不渲染参数行。</summary>
    public bool HasParameters => Parameters.Count > 0;

    /// <summary>侧栏空态：加载完且搜索无命中。</summary>
    public bool IsToolListEmpty => !IsLoading && !HasTools;

    /// <summary>空态文案：区分「没有插件」和「搜索没命中」，避免新拉仓库时误判为程序坏了。</summary>
    public string EmptyToolMessage => !_allTools.Any(IsWallTool)
        ? "没有加载到工具。开发环境请先构建解决方案（dotnet build Sparrow.slnx）。"
        : "没有匹配的工具";

    /// <summary>结果区空态：既没有结果也没有错误（错误会以红条显示）。</summary>
    public bool IsResultEmpty => !HasResult && !HasError;

    partial void OnHasToolsChanged(bool value) => OnPropertyChanged(nameof(IsToolListEmpty));

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsToolListEmpty));

    partial void OnHasResultChanged(bool value) => OnPropertyChanged(nameof(IsResultEmpty));

    partial void OnHasErrorChanged(bool value) => OnPropertyChanged(nameof(IsResultEmpty));

    /// <summary>工具正在执行：按钮置灰、文案切换，避免重复点击。</summary>
    [ObservableProperty]
    private bool _isExecuting;

    public string ExecuteLabel => IsExecuting
        ? string.IsNullOrWhiteSpace(ProgressText) ? "执行中…" : ProgressText
        : "执行";

    /// <summary>当前选中的工具可用，且没有在执行。</summary>
    public bool CanExecute => !IsExecuting && SelectedTool?.IsAvailable == true;

    /// <summary>选中工具不可用时的原因；可用时为空。</summary>
    public string? UnavailableText => SelectedTool?.UnavailableReason;

    /// <summary>执行中的一行进度（批量工具会填「正在处理 2/5」）。</summary>
    [ObservableProperty]
    private string? _progressText;

    partial void OnProgressTextChanged(string? value) => OnPropertyChanged(nameof(ExecuteLabel));
}
