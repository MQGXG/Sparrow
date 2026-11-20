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
        ClipboardPasteService? pasteService = null)
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
                settings);
        }
    }

    public SettingsViewModel Settings { get; }

    /// <summary>剪贴板页；测试或未接入监听时为 null。</summary>
    public ClipboardHistoryViewModel? Clipboard { get; }

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

    /// <summary>工具墙的扁平清单：搜索过滤后的全集；渲染按 <see cref="Groups"/> 分组进行。</summary>
    public ObservableCollection<ToolItemViewModel> VisibleTools { get; } = [];

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    public ObservableCollection<RecentToolViewModel> RecentTools { get; } = [];

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    public ObservableCollection<HistoryEntryViewModel> Recent { get; } = [];

    private List<HistoryEntryViewModel> _allHistory = [];

    /// <summary>工具全集缓存，侧栏搜索在这上面过滤，不重新扫插件。</summary>
    private List<ToolEntry> _allTools = [];

    /// <summary>手动折叠的分类；chip 选中时由 chip 规则接管。</summary>
    private readonly HashSet<string> _collapsedCategories = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string _historyQuery = string.Empty;

    /// <summary>顶部的工具搜索框：在工具墙上过滤。</summary>
    [ObservableProperty]
    private string _toolQuery = string.Empty;

    /// <summary>侧栏选中的分类；null = 全部。</summary>
    [ObservableProperty]
    private string? _selectedCategory;

    /// <summary>工作区三态：工具墙 / 最近使用 / 工具台。</summary>
    [ObservableProperty]
    private bool _isDetailOpen;

    /// <summary>最近使用页；和工具墙互斥，工具台优先。</summary>
    [ObservableProperty]
    private bool _isRecentOpen;

    /// <summary>剪贴板页；和工具墙 / 最近使用互斥，工具台优先。</summary>
    [ObservableProperty]
    private bool _isClipboardOpen;

    /// <summary>工具台从哪个页面进入，返回时回到原页面。</summary>
    private bool _detailFromRecent;

    public bool IsToolsSelected => !IsRecentOpen && !IsClipboardOpen;

    public bool IsWallVisible => !IsDetailOpen && !IsRecentOpen && !IsClipboardOpen;

    public bool IsRecentVisible => IsRecentOpen && !IsDetailOpen;

    public bool IsClipboardVisible => IsClipboardOpen && !IsDetailOpen;

    partial void OnIsRecentOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(IsWallVisible));
        OnPropertyChanged(nameof(IsRecentVisible));
        UpdateCategorySelection();
    }

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
        OnPropertyChanged(nameof(IsRecentVisible));
        OnPropertyChanged(nameof(IsClipboardVisible));
    }

    partial void OnToolQueryChanged(string value)
    {
        // 最近页 / 剪贴板页输入搜索词 = 想找别的工具，直接回工具墙看结果
        if (value.Length > 0)
        {
            IsRecentOpen = false;
            IsClipboardOpen = false;
        }

        RefilterTools();
    }

    partial void OnSelectedCategoryChanged(string? value)
    {
        // 选中状态只在 Categories 上，选完必须同步书签条，否则高亮会停在「全部」
        UpdateCategorySelection();
        // 分类过滤列表（和之前一致）；分组只负责视觉分节
        RefilterTools();
    }

    /// <summary>打开一个工具：进入工具台。</summary>
    public void OpenTool(ToolItemViewModel tool, bool fromRecent = false)
    {
        _detailFromRecent = fromRecent;
        SelectedTool = tool;
        IsDetailOpen = true;
        IsRecentOpen = false;
        IsClipboardOpen = false;
    }

    /// <summary>返回工具墙；从最近页进来的则回到最近页。</summary>
    public void CloseDetail()
    {
        IsDetailOpen = false;
        IsRecentOpen = _detailFromRecent;
    }

    /// <summary>侧栏点分类；name 为 null 表示「全部」。</summary>
    public void SelectCategory(string? name)
    {
        IsRecentOpen = false;
        IsClipboardOpen = false;
        SelectedCategory = name;
    }

    /// <summary>点组标题：折叠/展开该组。手动操作即退出 chip 联动模式，避免两套规则打架。</summary>
    public void ToggleGroup(string category)
    {
        SelectedCategory = null;

        if (!_collapsedCategories.Add(category))
            _collapsedCategories.Remove(category);

        UpdateGroupExpansion();
    }

    /// <summary>侧栏打开「最近使用」页。</summary>
    public void ShowRecent()
    {
        IsDetailOpen = false;
        IsRecentOpen = true;
        IsClipboardOpen = false;
    }

    /// <summary>切到剪贴板页；每次进入都重新读一遍，后台新复制的条目立刻能看到。</summary>
    public void ShowClipboard()
    {
        IsDetailOpen = false;
        IsRecentOpen = false;
        IsClipboardOpen = true;
        Clipboard?.Refresh();
    }

    /// <summary>侧栏回到工具墙。</summary>
    public void ShowTools()
    {
        IsDetailOpen = false;
        IsRecentOpen = false;
        IsClipboardOpen = false;
    }

    public bool HasRecentTools => RecentTools.Count > 0;

    /// <summary>输入框提示：图片集 / 拖入的图片 / 文件清单 / 工具输入形态，依次给出说明。</summary>
    public string InputHint => IsImageInput && HasDroppedImage
        ? $"将使用拖入的图片：{_droppedImageName}"
        : IsImagesInput
            ? InputImageSummary
            : IsFilesInput
                ? DroppedFileSummary
                : SelectedTool?.Entry.Descriptor.Accepts switch
                {
                    InputShape.Image => "将使用剪贴板里的图片",
                    InputShape.None => "此工具不需要输入",
                    _ => "输入内容…"
                };

    /// <summary>图片集提示：张数 + 前三个文件名，其余折叠。</summary>
    private string InputImageSummary
    {
        get
        {
            if (InputImages.Count == 0)
                return "把图片拖进窗口，或点「添加图片…」（可多选）";

            var names = InputImages
                .Take(3)
                .Select(image => image.Name)
                .Where(name => name.Length > 0);

            var text = string.Join("、", names);
            return InputImages.Count > 3
                ? $"将识别 {InputImages.Count} 张图片：{text} 等"
                : $"将识别 {InputImages.Count} 张图片：{text}";
        }
    }

    /// <summary>文件输入工具的提示：列出前几个文件名，其余折叠成「等 N 个」。</summary>
    private string DroppedFileSummary
    {
        get
        {
            if (_droppedFiles.Count == 0)
                return "把文件拖进窗口，或点「选择文件…」（可多选）";

            var names = _droppedFiles
                .Select(Path.GetFileName)
                .Take(3)
                .Select(name => name ?? string.Empty);

            var text = string.Join("、", names);
            return _droppedFiles.Count > 3
                ? $"将处理 {_droppedFiles.Count} 个文件：{text} 等"
                : $"将处理 {_droppedFiles.Count} 个文件：{text}";
        }
    }

    public bool IsTextInput => SelectedTool?.Entry.Descriptor.Accepts is InputShape.Text or InputShape.RichText;

    public bool IsImageInput => SelectedTool?.Entry.Descriptor.Accepts == InputShape.Image;

    /// <summary>文件类工具（批量识别等）：输入来自拖入或选择的多个文件。</summary>
    public bool IsFilesInput => SelectedTool?.Entry.Descriptor.Accepts == InputShape.Files;

    /// <summary>图片集工具（OCR）：输入是 1..N 张图片，来源不限。</summary>
    public bool IsImagesInput => SelectedTool?.Entry.Descriptor.Accepts == InputShape.Images;

    /// <summary>图片集里的图片；缩略图条直接绑定它。</summary>
    public ObservableCollection<InputImageViewModel> InputImages { get; } = [];

    public int InputImageCount => InputImages.Count;

    /// <summary>当前轮播到第几张（从 0 起）。</summary>
    [ObservableProperty]
    private int _currentImageIndex;

    public bool HasMultipleImages => InputImages.Count > 1;

    public bool CanGoPreviousImage => CurrentImageIndex > 0;

    public bool CanGoNextImage => CurrentImageIndex < InputImages.Count - 1;

    /// <summary>当前显示的那张原图。</summary>
    public InputImageViewModel? CurrentImage =>
        CurrentImageIndex >= 0 && CurrentImageIndex < InputImages.Count
            ? InputImages[CurrentImageIndex]
            : null;

    public string ImageCounter => InputImages.Count == 0
        ? string.Empty
        : $"{CurrentImageIndex + 1} / {InputImages.Count}";

    public void PreviousImage()
    {
        if (CanGoPreviousImage)
            CurrentImageIndex--;
    }

    public void NextImage()
    {
        if (CanGoNextImage)
            CurrentImageIndex++;
    }

    /// <summary>点小缩略图直接跳到某张。</summary>
    public void SelectImage(InputImageViewModel image)
    {
        var index = InputImages.IndexOf(image);
        if (index >= 0)
            CurrentImageIndex = index;
    }

    partial void OnCurrentImageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoPreviousImage));
        OnPropertyChanged(nameof(CanGoNextImage));
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(ImageCounter));
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(VisibleSections));
    }

    /// <summary>
    /// 多图结果：false = 只看当前这张（默认，方便逐张核对）；
    /// true = 看全部（总览 / 复制全部）。
    /// </summary>
    [ObservableProperty]
    private bool _showAllSections;

    public bool IsCurrentSectionView => !ShowAllSections;

    /// <summary>只有一段时两个视图内容一样，切换按钮不该出现。</summary>
    public bool CanSwitchSectionView => ResultSections.Count > 1;

    /// <summary>当前轮播图对应的那段识别结果。</summary>
    public TextSectionViewModel? CurrentSection =>
        ResultSections.FirstOrDefault(section => section.SourceIndex == CurrentImageIndex)
        ?? ResultSections.FirstOrDefault();

    partial void OnShowAllSectionsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCurrentSectionView));
        OnPropertyChanged(nameof(VisibleSections));
    }

    /// <summary>
    /// 结果区实际渲染的段：只看当前时是一个元素，看全部时是整份列表。
    /// 两个视图共用同一批 TextSectionViewModel，不复制数据。
    /// </summary>
    public IReadOnlyList<TextSectionViewModel> VisibleSections =>
        ShowAllSections
            ? ResultSections
            : CurrentSection is { } current
                ? [current]
                : [];

    public bool HasInputImages => InputImages.Count > 0;

    public bool CanClearInputImages => IsImagesInput && HasInputImages;

    /// <summary>页内大图预览（点缩略图打开）。</summary>
    [ObservableProperty]
    private bool _isImagePreviewVisible;

    [ObservableProperty]
    private byte[]? _previewImagePng;

    public int DroppedFileCount => _droppedFiles.Count;

    /// <summary>只有当前是图片工具且确实拖入了图片，才显示「清除」。</summary>
    public bool CanClearDroppedImage => HasDroppedImage && IsImageInput;

    /// <summary>文件工具的「清除」：清空本次选择的文件清单。</summary>
    public bool CanClearDroppedFiles => IsFilesInput && _droppedFiles.Count > 0;

    partial void OnHistoryQueryChanged(string value) => FilterHistory();

    private void FilterHistory()
    {
        Recent.Clear();

        var query = HistoryQuery.Trim();
        var items = string.IsNullOrEmpty(query)
            ? _allHistory
            : _allHistory
                .Where(item =>
                    item.ToolName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Entry.Output.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        HasHistory = items.Count > 0;

        foreach (var item in items)
            Recent.Add(item);
    }

    [ObservableProperty]
    private ToolItemViewModel? _selectedTool;

    [ObservableProperty]
    private string _inputText = string.Empty;

    private const long MaxDroppedImageBytes = 20 * 1024 * 1024;
    private const long MaxDroppedTextBytes = 2 * 1024 * 1024;

    /// <summary>图片集上限：再多就不是「顺手识别」，而是批量处理产品了。</summary>
    private const int MaxInputImageCount = 50;

    private const long MaxInputImagesTotalBytes = 200L * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".log", ".yaml", ".yml",
        ".ini", ".toml", ".cs", ".js", ".ts", ".py", ".html", ".css", ".sql"
    };

    private byte[]? _droppedImagePng;
    private ImageFormat _droppedImageFormat = ImageFormat.Png;
    private string? _droppedImageName;

    /// <summary>拖入 / 选中的文件清单，供 Files 输入的工具使用（批量识别等）。</summary>
    private readonly List<string> _droppedFiles = [];

    /// <summary>执行代次：用来丢弃上一次执行迟到的进度回调。</summary>
    private int _progressGeneration;

    /// <summary>是否已经拖入一张图片；图片工具执行时优先用它，而不是剪贴板。</summary>
    [ObservableProperty]
    private bool _hasDroppedImage;

    partial void OnHasDroppedImageChanged(bool value)
    {
        OnPropertyChanged(nameof(InputHint));
        OnPropertyChanged(nameof(CanClearDroppedImage));
    }

    [ObservableProperty]
    private string? _resultText;

    /// <summary>
    /// 多图识别的分段结果。只有一段时不渲染列表——单张图套个列表反而比纯文本难读。
    /// </summary>
    public ObservableCollection<TextSectionViewModel> ResultSections { get; } = [];

    [ObservableProperty]
    private bool _hasSections;

    /// <summary>SVG 源码；非空表示当前结果需要预览/源码双视图。</summary>
    [ObservableProperty]
    private string? _svgSource;

    /// <summary>渲染好的 PNG；为空表示渲染失败，界面回退到源码。</summary>
    [ObservableProperty]
    private byte[]? _svgPreviewPng;

    [ObservableProperty]
    private string? _svgPreviewError;

    /// <summary>SVG 结果当前显示源码而不是预览。</summary>
    [ObservableProperty]
    private bool _showSvgSource;

    public bool HasSvgResult => SvgSource is not null;

    public bool IsSvgPreviewVisible => HasSvgResult && !ShowSvgSource;

    public bool IsSvgSourceVisible => HasSvgResult && ShowSvgSource;

    public bool IsTextResultVisible => !HasSvgResult && !HasImageResult && !HasSections;

    /// <summary>图片结果（缩放 / 格式转换）；非空时结果区显示位图预览。</summary>
    [ObservableProperty]
    private byte[]? _imageResultPng;

    [ObservableProperty]
    private ImageFormat _imageResultFormat = ImageFormat.Png;

    [ObservableProperty]
    private bool _hasImageResult;

    partial void OnSvgSourceChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSvgResult));
        OnPropertyChanged(nameof(IsSvgPreviewVisible));
        OnPropertyChanged(nameof(IsSvgSourceVisible));
        OnPropertyChanged(nameof(IsTextResultVisible));
    }

    partial void OnShowSvgSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSvgPreviewVisible));
        OnPropertyChanged(nameof(IsSvgSourceVisible));
    }

    partial void OnHasImageResultChanged(bool value) => OnPropertyChanged(nameof(IsTextResultVisible));

    partial void OnHasSectionsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTextResultVisible));
        OnPropertyChanged(nameof(CanSwitchSectionView));
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(VisibleSections));
        OnPropertyChanged(nameof(CopyAllHint));
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
    public string EmptyToolMessage => _allTools.Count == 0
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

    /// <summary>过滤后的历史是否为空，用于空状态提示。</summary>
    [ObservableProperty]
    private bool _hasHistory;

    public string ExecuteLabel => IsExecuting
        ? string.IsNullOrWhiteSpace(ProgressText) ? "执行中…" : ProgressText
        : "执行";

    /// <summary>执行中的一行进度（批量工具会填「正在处理 2/5」）。</summary>
    [ObservableProperty]
    private string? _progressText;

    partial void OnProgressTextChanged(string? value) => OnPropertyChanged(nameof(ExecuteLabel));

    /// <summary>
    /// 每次打开工作区时刷新工具列表与剪贴板输入。
    ///
    /// 插件加载（首次可能是 500 MB 的 OCR）放在后台线程，
    /// 窗口先显示、工具列表后填充，避免打开工作区时 UI 卡 2–3 秒。
    /// </summary>
    public async Task ReloadAsync()
    {
        if (IsLoading)
            return;

        IsLoading = true;
        ClearResult();

        try
        {
            var entries = await Task.Run(() =>
                _catalog.GetRegistry().Search(null, int.MaxValue));

            _allTools = [.. entries];
            RebuildCategories();
            RefilterTools();

            SelectedTool = Groups.SelectMany(group => group.Tools).FirstOrDefault();
            IsDetailOpen = false;
            IsRecentOpen = false;
            InputText = _clipboard.GetText() ?? string.Empty;

            _allHistory = _history.Recent(200)
                .Select(entry => new HistoryEntryViewModel(entry))
                .ToList();

            FilterHistory();

            // 必须等 _allHistory 载入之后再拼「最近使用」，否则启动时永远是空的
            RebuildRecentTools();
        }
        catch (Exception ex)
        {
            _log.Error("加载工具失败", ex);
            ShowError($"加载工具失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedToolChanged(ToolItemViewModel? oldValue, ToolItemViewModel? newValue)
    {
        // 切走之前把旧工具的参数落盘，用户「选完语言直接返回」也能记住
        if (oldValue is not null)
        {
            PersistParameters(oldValue);
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
            newValue.IsSelected = true;

        LoadParameters(newValue);
        OnPropertyChanged(nameof(InputHint));
        OnPropertyChanged(nameof(IsTextInput));
        OnPropertyChanged(nameof(IsImageInput));
        OnPropertyChanged(nameof(IsFilesInput));
        OnPropertyChanged(nameof(IsImagesInput));
        OnPropertyChanged(nameof(HasInputImages));
        OnPropertyChanged(nameof(InputImageCount));
        OnPropertyChanged(nameof(CanClearInputImages));
        OnPropertyChanged(nameof(CanClearDroppedImage));
        OnPropertyChanged(nameof(CanClearDroppedFiles));
    }

    /// <summary>工具墙：先按分类过滤（和之前一致），再按搜索词过滤；分组只负责视觉分节。</summary>
    private void RefilterTools()
    {
        IEnumerable<ToolEntry> items = SelectedCategory is null
            ? _allTools
            : _allTools.Where(entry => entry.Descriptor.Category == SelectedCategory);

        var query = ToolQuery.Trim();
        if (query.Length > 0)
        {
            items = items.Where(entry =>
                entry.Descriptor.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Descriptor.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Descriptor.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = items.ToList();
        var tools = list.Select(entry => new ToolItemViewModel(entry)).ToList();

        Groups.Clear();
        VisibleTools.Clear();

        foreach (var tool in tools)
            VisibleTools.Add(tool);

        foreach (var group in tools
                     .GroupBy(tool => tool.CategoryLabel)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Groups.Add(new ToolGroupViewModel(group.Key, group));
        }

        HasTools = list.Count > 0;
        UpdateGroupExpansion();
        OnPropertyChanged(nameof(EmptyToolMessage));
    }

    /// <summary>组装展开状态：搜索时强制全展开；选中分类时该组展开；其余按手动折叠记忆。</summary>
    private void UpdateGroupExpansion()
    {
        var hasQuery = ToolQuery.Trim().Length > 0;

        foreach (var group in Groups)
        {
            group.IsExpanded = hasQuery
                || (SelectedCategory is not null && group.Category == SelectedCategory)
                || !_collapsedCategories.Contains(group.Category);
        }
    }

    /// <summary>侧栏分类导航：全部 + 各分类（含工具数）。</summary>
    private void RebuildCategories()
    {
        Categories.Clear();
        Categories.Add(new CategoryViewModel("全部", _allTools.Count));

        foreach (var group in _allTools
                     .GroupBy(entry => entry.Descriptor.Category)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Categories.Add(new CategoryViewModel(group.Key, group.Count()));
        }

        UpdateCategorySelection();
    }

    private void UpdateCategorySelection()
    {
        foreach (var category in Categories)
            category.IsSelected = !IsRecentOpen && !IsClipboardOpen
                && (SelectedCategory is null
                    ? category.Name == "全部"
                    : category.Name == SelectedCategory);
    }

    /// <summary>
    /// 工具墙顶部的「最近使用」：按执行历史去重取前 5 个还存在的工具。
    /// </summary>
    private void RebuildRecentTools()
    {
        RecentTools.Clear();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var history in _allHistory)
        {
            if (!seen.Add(history.ToolName))
                continue;

            var entry = _allTools.Find(tool =>
                tool.Descriptor.Name == history.ToolName);

            if (entry is null)
                continue;

            RecentTools.Add(new RecentToolViewModel(new ToolItemViewModel(entry), history.TimeLabel));

            if (RecentTools.Count >= 5)
                break;
        }

        OnPropertyChanged(nameof(HasRecentTools));
    }

    partial void OnIsExecutingChanged(bool value) => OnPropertyChanged(nameof(ExecuteLabel));

    private void LoadParameters(ToolItemViewModel? tool)
    {
        Parameters.Clear();

        if (tool is null)
        {
            OnPropertyChanged(nameof(HasParameters));
            return;
        }

        var saved = _parameters?.Get(tool.Entry.Descriptor.Id);

        // 参数控件完全由声明生成，插件不提供任何 UI
        foreach (var parameter in tool.Entry.Descriptor.Parameters)
        {
            var item = new ParameterViewModel(parameter);

            // 上次保存的值优先；Choice 必须仍在候选里，避免插件改选项后带进脏值
            if (saved is not null
                && saved.TryGetValue(parameter.Key, out var value)
                && (parameter.Choices.Length == 0 || parameter.Choices.Contains(value)))
            {
                item.Value = value;
            }

            Parameters.Add(item);
        }

        OnPropertyChanged(nameof(HasParameters));
    }

    /// <summary>把当前参数写到参数存储；工具没参数或没接入存储时直接跳过。</summary>
    private void PersistParameters(ToolItemViewModel tool)
    {
        if (_parameters is null || Parameters.Count == 0)
            return;

        var values = Parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => parameter.Value,
            StringComparer.Ordinal);

        _parameters.Save(tool.Entry.Descriptor.Id, values);
    }

    public async Task ExecuteAsync()
    {
        if (IsExecuting || SelectedTool is null)
            return;

        var entry = SelectedTool.Entry;

        var input = BuildInput(entry.Descriptor, out var inputError);
        if (input is null)
        {
            ShowError(inputError ?? "无法构造输入");
            return;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
            parameters[parameter.Key] = parameter.Value;

        // 执行即记忆：下次打开这个工具时参数保持上次的值
        _parameters?.Save(entry.Descriptor.Id, parameters);

        IsExecuting = true;
        ProgressText = null;

        // 只让本次执行的进度更新界面：执行结束后迟到的回调不能污染下一次
        var generation = ++_progressGeneration;
        var progress = new Progress<ToolProgress>(update =>
        {
            if (generation != _progressGeneration)
                return;

            ProgressText = update switch
            {
                { Message: { Length: > 0 } message, Percent: { } percent }
                    => $"{message}（{percent * 100:F0}%）",
                { Message: { Length: > 0 } message } => message,
                { Percent: { } percent } => $"执行中… {percent * 100:F0}%",
                _ => null
            };
        });

        try
        {
            var context = new ToolContext
            {
                Input = input,
                Source = input is FileSetInput or ImageSetInput
                    ? ToolSource.Dropped
                    : ToolSource.Clipboard,
                Parameters = parameters,
                Host = entry.Host,
                Progress = progress,
                Files = input is FileSetInput files ? new ScopedFileAccess(files.Paths) : null
            };

            var startedAt = Stopwatch.GetTimestamp();
            var result = await entry.Tool.ExecuteAsync(context, CancellationToken.None);
            var elapsed = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            if (result.Success && result.Payload is not null)
            {
                switch (result.Payload)
                {
                    case SvgPayload svgPayload:
                        await ShowSvgResultAsync(entry, context, svgPayload, elapsed);
                        return;

                    case OcrResultPayload ocrPayload when ocrPayload.Sections is { Count: > 1 } sections:
                        // 多图识别：渲染成分段列表，每段能对上是哪张图
                        ShowSectionResult(entry, context, ocrPayload, sections, elapsed);
                        return;

                    case TextPayload or OcrResultPayload:
                        var text = result.Payload switch
                        {
                            TextPayload textPayload => textPayload.Text,
                            OcrResultPayload ocrPayload => ocrPayload.Text,
                            _ => string.Empty
                        };

                        ShowTextResult(entry, context, text, elapsed);
                        return;

                    case ImagePayload imagePayload:
                        ShowImageResult(entry, context, imagePayload, elapsed);
                        return;

                    case TablePayload tablePayload:
                        ShowTextResult(entry, context, ResultTextFormatter.Format(tablePayload), elapsed);
                        return;

                    case FileSetPayload fileSetPayload:
                        ShowTextResult(entry, context, ResultTextFormatter.Format(fileSetPayload), elapsed);
                        return;

                    default:
                        // 新载荷类型不该被当成失败：成功就是成功，只是宿主还没做专用渲染
                        ShowTextResult(
                            entry,
                            context,
                            $"（{result.Payload.GetType().Name} 结果，暂不支持展示）",
                            elapsed);
                        return;
                }
            }

            ShowError(result.Error?.Message ?? "执行失败");
        }
        catch (Exception ex)
        {
            // 插件异常不能带走工作区
            _log.Error("工作区执行失败", ex);
            ShowError($"工具执行异常：{ex.Message}");
        }
        finally
        {
            _progressGeneration++;
            ProgressText = null;
            IsExecuting = false;
        }
    }

    private void ShowTextResult(ToolEntry entry, ToolContext context, string text, long elapsed)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;

        // 单段结果或普通文本：清掉上一次的分段列表，避免列表残留
        ResultSections.Clear();
        HasSections = false;

        ResultText = text;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, text, elapsed);
    }

    /// <summary>
    /// 多图识别的分段结果。列表负责「哪张图 / 哪段文字 / 哪张失败」，
    /// 纯文本仍留在 <see cref="ResultText"/> 里——复制全部和历史用的就是它。
    /// </summary>
    private void ShowSectionResult(
        ToolEntry entry,
        ToolContext context,
        OcrResultPayload payload,
        IReadOnlyList<TextSection> sections,
        long elapsed)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;

        ResultSections.Clear();
        foreach (var section in sections)
        {
            // 按 SourceIndex 找回输入里的原图；找不到就只显示文字，
            // 插件之间不传图片字节，避免同一张图在内存里存两份
            var source = section.SourceIndex >= 0 && section.SourceIndex < InputImages.Count
                ? InputImages[section.SourceIndex]
                : null;

            ResultSections.Add(new TextSectionViewModel(section, source));
        }

        HasSections = true;

        // 新结果出来时回到「只看当前这张」，用户刚识别完通常要逐张核对
        ShowAllSections = false;
        CurrentImageIndex = Math.Clamp(CurrentImageIndex, 0, Math.Max(0, InputImages.Count - 1));

        ResultText = payload.Text;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, payload.Text, elapsed);
    }

    /// <summary>
    /// 图片结果：预览位图，历史只记摘要（不把字节灌进 JSONL）。
    /// 点「复制」回到剪贴板，点「保存」由视图层弹文件对话框落盘。
    /// </summary>
    private void ShowImageResult(ToolEntry entry, ToolContext context, ImagePayload payload, long elapsed)
    {
        ClearSvgResult();

        ImageResultPng = payload.Data.ToArray();
        ImageResultFormat = payload.Format;
        HasImageResult = true;

        var summary = SummarizeImage(payload);
        ResultText = summary;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, summary, elapsed);
    }

    private async Task ShowSvgResultAsync(
        ToolEntry entry,
        ToolContext context,
        SvgPayload payload,
        long elapsed)
    {
        var sanitized = SvgSanitizer.Sanitize(payload.Svg);
        if (!sanitized.Success)
        {
            ShowError(sanitized.Error ?? "SVG 无法预览");
            return;
        }

        var svg = sanitized.Svg!;

        ResultText = svg;
        SvgSource = svg;
        SvgPreviewPng = null;
        SvgPreviewError = null;
        ShowSvgSource = false;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        SvgPreviewPng = await _svgPreview.RenderAsync(svg, CancellationToken.None);

        if (SvgPreviewPng is null)
        {
            SvgPreviewError = "预览渲染失败，已显示源码";
            ShowSvgSource = true;
        }

        AddHistory(entry, context, svg, elapsed);
    }

    private void AddHistory(ToolEntry entry, ToolContext context, string output, long elapsed)
    {
        var historyEntry = new HistoryEntry(
            Guid.NewGuid().ToString("N"),
            entry.Descriptor.Id,
            entry.Descriptor.Name,
            output,
            PreviewInput(context.Input),
            DateTimeOffset.Now,
            elapsed);

        _history.Add(historyEntry);
        _allHistory.Insert(0, new HistoryEntryViewModel(historyEntry));
        FilterHistory();
        RebuildRecentTools();
    }

    /// <summary>右上角复制的说明：多图时明确写出「全部 N 张」，避免和每列的单条复制混淆。</summary>
    public string CopyAllHint => HasSections
        ? $"复制全部 {ResultSections.Count} 张的识别结果"
        : "复制结果到剪贴板";

    /// <summary>复制某一段的识别结果（含译文，如果选了翻译）。</summary>
    public void CopySectionText(TextSectionViewModel section)
    {
        if (string.IsNullOrWhiteSpace(section.Text))
        {
            _log.Warn("这一条没有可复制的文字");
            return;
        }

        if (_clipboard.SetText(section.Text))
            _log.Info($"已复制「{section.Title}」：{section.Text.Length} 字");
    }

    public void CopyResult()
    {
        // 图片结果复制回剪贴板（JPEG/WebP 先转 PNG，写剪贴板只认 PNG）
        if (HasImageResult && ImageResultPng is { Length: > 0 } bytes)
        {
            var png = ImageResultFormat == ImageFormat.Png ? bytes : ImageBytes.ToPng(bytes);
            if (png is null)
            {
                _log.Warn("图片结果转 PNG 失败，无法复制");
                return;
            }

            if (_clipboard.SetImagePng(png))
                _log.Info($"图片结果已复制：{png.Length} 字节");
            else
                _log.Warn("图片结果写入剪贴板失败");

            return;
        }

        if (!string.IsNullOrEmpty(ResultText))
            _clipboard.SetText(ResultText);
    }

    /// <summary>点击历史记录：把内容放回结果区和剪贴板。</summary>
    public void CopyHistory(HistoryEntryViewModel history)
    {
        ResultText = history.Entry.Output;
        HasResult = true;
        HasError = false;
        ErrorText = null;
        _clipboard.SetText(history.Entry.Output);
    }

    public void ClearHistory()
    {
        _history.Clear();
        _allHistory.Clear();
        Recent.Clear();
        RecentTools.Clear();
        HasHistory = false;
        OnPropertyChanged(nameof(HasRecentTools));
    }

    public void ShowSettings() => IsSettingsVisible = true;

    public void HideSettings() => IsSettingsVisible = false;

    /// <summary>
    /// 处理拖入的文件：图片进图片集 / 单图输入，多文件进文件清单，
    /// 文本文件填输入框。分流规则见 docs/plans/2026-11-18-ocr-image-set-design.md。
    /// </summary>
    public void AcceptDroppedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        var imagePaths = paths
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .ToList();

        // 全是图片：交给图片集（OCR 这类工具），不再要求用户先选对卡片
        if (imagePaths.Count == paths.Count)
        {
            AddInputImages(paths);

            if (InputImages.Count == 0)
                return;

            // 当前是单图工具：保留「拖入图片优先」的老行为
            if (IsImageInput)
                SetDroppedImage(imagePaths[0]);
            else if (!IsImagesInput)
                OpenFirstMatching(InputShape.Images);

            return;
        }

        SetDroppedFiles(paths);

        // 当前就在图片集工具里：混合文件也只收图片，不跳走
        if (IsImagesInput)
        {
            if (imagePaths.Count > 0)
                AddInputImages(imagePaths);

            return;
        }

        // 当前就是文件类工具：多选文件全部留给它，不做单文件特化
        if (IsFilesInput)
            return;

        // 混合 / 多选：有文件类工具就直接切过去
        if (paths.Count > 1 && OpenFirstMatching(InputShape.Files))
            return;

        var textPath = paths.FirstOrDefault(path => TextExtensions.Contains(Path.GetExtension(path)));
        if (textPath is not null)
        {
            try
            {
                var info = new FileInfo(textPath);
                if (info.Length > MaxDroppedTextBytes)
                {
                    ShowError("文本文件超过 2 MB，请先拆分");
                    return;
                }

                InputText = File.ReadAllText(textPath);

                if (!IsTextInput)
                    OpenFirstMatching(InputShape.Text);

                return;
            }
            catch (Exception ex)
            {
                ShowError($"读取文本失败：{ex.Message}");
                return;
            }
        }

        ShowError("只支持拖入图片或文本文件");
    }

    /// <summary>把一张图片设为单图工具的输入（拖入优先于剪贴板）。</summary>
    private void SetDroppedImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxDroppedImageBytes)
            {
                ShowError("图片超过 20 MB，请先压缩");
                return;
            }

            _droppedImagePng = File.ReadAllBytes(path);
            _droppedImageFormat = FormatFromExtension(Path.GetExtension(path));
            _droppedImageName = Path.GetFileName(path);
            HasDroppedImage = true;
        }
        catch (Exception ex)
        {
            ShowError($"读取图片失败：{ex.Message}");
        }
    }

    /// <summary>「添加图片…」的结果：加入图片集，必要时切到图片集工具。</summary>
    public void AcceptPickedImages(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        AddInputImages(paths);

        if (InputImages.Count > 0 && !IsImagesInput)
            OpenFirstMatching(InputShape.Images);
    }

    /// <summary>
    /// 把磁盘图片加入图片集；返回实际加入数量。
    /// 非图片、超限、重复路径都会跳过并说明，不让一次坏文件毁掉整次选择。
    /// </summary>
    public int AddInputImages(IReadOnlyList<string> paths)
    {
        var added = 0;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(path)))
                continue;

            if (InputImages.Count >= MaxInputImageCount)
            {
                ShowError($"最多一次处理 {MaxInputImageCount} 张图片");
                break;
            }

            if (InputImages.Any(image =>
                    string.Equals(image.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var info = new FileInfo(path);

                if (info.Length > MaxDroppedImageBytes)
                {
                    ShowError($"「{info.Name}」超过 20 MB，已跳过");
                    continue;
                }

                var total = InputImages.Sum(image => (long)image.Input.ByteSize) + info.Length;
                if (total > MaxInputImagesTotalBytes)
                {
                    ShowError("图片总大小超过 200 MB，已停止添加");
                    break;
                }

                var bytes = File.ReadAllBytes(path);
                var (width, height) = ImageSizeReader.Read(bytes);
                var input = new ImageInput(bytes, FormatFromExtension(Path.GetExtension(path)))
                {
                    Name = info.Name,
                    Width = width,
                    Height = height
                };

                InputImages.Add(new InputImageViewModel(input, path));
                added++;
            }
            catch (Exception ex)
            {
                ShowError($"读取图片失败：{ex.Message}");
            }
        }

        NotifyInputImagesChanged();
        return added;
    }

    /// <summary>把剪贴板图片加入图片集。</summary>
    public void AddClipboardImage()
    {
        var png = _clipboard.GetImagePng();
        if (png is null)
        {
            ShowError("剪贴板里没有图片");
            return;
        }

        if (InputImages.Count >= MaxInputImageCount)
        {
            ShowError($"最多一次处理 {MaxInputImageCount} 张图片");
            return;
        }

        var name = InputImages.Count == 0
            ? "剪贴板图片"
            : $"剪贴板图片 {InputImages.Count + 1}";

        var (imageWidth, imageHeight) = ImageSizeReader.Read(png);
        InputImages.Add(new InputImageViewModel(
            new ImageInput(png, ImageFormat.Png)
            {
                Name = name,
                Width = imageWidth,
                Height = imageHeight
            },
            sourcePath: null));

        NotifyInputImagesChanged();

        if (!IsImagesInput)
            OpenFirstMatching(InputShape.Images);
    }

    public void RemoveInputImage(InputImageViewModel image)
    {
        if (!InputImages.Remove(image))
            return;

        if (IsImagePreviewVisible)
            HideImagePreview();

        NotifyInputImagesChanged();
    }

    public void ClearInputImages()
    {
        if (InputImages.Count == 0)
            return;

        InputImages.Clear();
        HideImagePreview();
        NotifyInputImagesChanged();
    }

    public void ShowImagePreview(InputImageViewModel image)
    {
        PreviewImagePng = image.PreviewBytes;
        IsImagePreviewVisible = true;
    }

    public void HideImagePreview()
    {
        IsImagePreviewVisible = false;
        PreviewImagePng = null;
    }

    private void NotifyInputImagesChanged()
    {
        OnPropertyChanged(nameof(InputImageCount));
        OnPropertyChanged(nameof(HasInputImages));
        OnPropertyChanged(nameof(CanClearInputImages));
        OnPropertyChanged(nameof(InputHint));

        // 轮播相关：数量变了，索引可能要夹回合法范围
        OnPropertyChanged(nameof(HasMultipleImages));
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(ImageCounter));
        OnPropertyChanged(nameof(CanGoPreviousImage));
        OnPropertyChanged(nameof(CanGoNextImage));
        OnPropertyChanged(nameof(CurrentSection));

        if (CurrentImageIndex >= InputImages.Count)
            CurrentImageIndex = Math.Max(0, InputImages.Count - 1);
    }

    public void ClearDroppedImage()
    {
        _droppedImagePng = null;
        _droppedImageName = null;
        HasDroppedImage = false;
    }

    /// <summary>「选择文件…」的结果：和拖入走同一条记录路径。</summary>
    public void AcceptPickedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        SetDroppedFiles(paths);

        // 从文件工具的选择按钮进来时已经在对的地方；不是则切过去
        if (!IsFilesInput)
            OpenFirstMatching(InputShape.Files);
    }

    public void ClearDroppedFiles()
    {
        if (_droppedFiles.Count == 0)
            return;

        _droppedFiles.Clear();
        NotifyFilesChanged();
    }

    /// <summary>整体替换文件清单；去重并保留用户的选择顺序。</summary>
    private void SetDroppedFiles(IEnumerable<string> paths)
    {
        _droppedFiles.Clear();
        _droppedFiles.AddRange(paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        NotifyFilesChanged();
    }

    private void NotifyFilesChanged()
    {
        OnPropertyChanged(nameof(DroppedFileCount));
        OnPropertyChanged(nameof(InputHint));
        OnPropertyChanged(nameof(CanClearDroppedFiles));
    }

    private bool OpenFirstMatching(InputShape shape)
    {
        var entry = _allTools.FirstOrDefault(tool => tool.Descriptor.Accepts == shape);
        if (entry is null)
            return false;

        OpenTool(new ToolItemViewModel(entry));
        return true;
    }

    private static ImageFormat FormatFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.Webp,
        ".bmp" => ImageFormat.Bmp,
        ".gif" => ImageFormat.Gif,
        _ => ImageFormat.Png
    };

    private IToolInput? BuildInput(ToolDescriptor descriptor, out string? error)
    {
        error = null;

        if (descriptor.Accepts == InputShape.None)
            return EmptyInput.Instance;

        if (descriptor.Accepts == InputShape.Files)
        {
            if (_droppedFiles.Count == 0)
            {
                error = "还没有文件。把文件拖进窗口，或点「选择文件…」。";
                return null;
            }

            return new FileSetInput(_droppedFiles.ToArray());
        }

        if (descriptor.Accepts == InputShape.Images)
        {
            if (InputImages.Count == 0)
            {
                error = "还没有图片。把图片拖进窗口，或点「添加图片…」。";
                return null;
            }

            return new ImageSetInput(InputImages.Select(image => image.Input).ToArray());
        }

        if (descriptor.Accepts == InputShape.Image)
        {
            // 拖入的图片优先；没有才回退到剪贴板
            if (_droppedImagePng is not null)
                return WithSize(new ImageInput(_droppedImagePng, _droppedImageFormat));

            var png = _clipboard.GetImagePng();
            if (png is null)
            {
                error = "剪贴板中没有图片。可以先复制图片，或直接把图片拖进窗口。";
                return null;
            }

            return WithSize(new ImageInput(png, ImageFormat.Png));
        }

        return new TextInput(InputText);
    }

    /// <summary>补上图片宽高：只读文件头，不解码像素。</summary>
    private static ImageInput WithSize(ImageInput input)
    {
        var (width, height) = ImageSizeReader.Read(input.Data.Span);
        return input with { Width = width, Height = height };
    }

    private static string PreviewInput(IToolInput input) => input switch
    {
        TextInput text => text.Text.Length <= 120 ? text.Text : text.Text[..120] + "…",
        ImageInput image => $"图片 {image.ByteSize} 字节",
        ImageSetInput images => $"{images.Images.Count} 张图片",
        FileSetInput files => $"{files.Paths.Count} 个文件",
        _ => input.GetType().Name
    };

    private void ShowError(string message)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ErrorText = message;
        HasError = true;
        HasResult = false;
        ResultText = null;
    }

    private void ClearResult()
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ResultText = null;
        ErrorText = null;
        HasResult = false;
        HasError = false;

        ResultSections.Clear();
        HasSections = false;
        ShowAllSections = false;
    }

    private void ClearSvgResult()
    {
        SvgSource = null;
        SvgPreviewPng = null;
        SvgPreviewError = null;
        ShowSvgSource = false;
    }
}
