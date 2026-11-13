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
        SvgPreviewService? svgPreview = null)
    {
        _catalog = catalog;
        _clipboard = clipboard;
        _log = log;
        _history = history;
        _parameters = parameters;
        _svgPreview = svgPreview ?? new SvgPreviewService();
        Settings = settings;
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

    /// <summary>工具墙的扁平网格：分类只作为卡片上的标签，不再按分类分节。</summary>
    public ObservableCollection<ToolItemViewModel> VisibleTools { get; } = [];

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    public ObservableCollection<RecentToolViewModel> RecentTools { get; } = [];

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    public ObservableCollection<HistoryEntryViewModel> Recent { get; } = [];

    private List<HistoryEntryViewModel> _allHistory = [];

    /// <summary>工具全集缓存，侧栏搜索在这上面过滤，不重新扫插件。</summary>
    private List<ToolEntry> _allTools = [];

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

    /// <summary>工具台从哪个页面进入，返回时回到原页面。</summary>
    private bool _detailFromRecent;

    public bool IsToolsSelected => !IsRecentOpen;

    public bool IsWallVisible => !IsDetailOpen && !IsRecentOpen;

    public bool IsRecentVisible => IsRecentOpen && !IsDetailOpen;

    partial void OnIsRecentOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(IsWallVisible));
        OnPropertyChanged(nameof(IsRecentVisible));
        UpdateCategorySelection();
    }

    partial void OnIsDetailOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWallVisible));
        OnPropertyChanged(nameof(IsRecentVisible));
    }

    partial void OnToolQueryChanged(string value) => RefilterTools();

    partial void OnSelectedCategoryChanged(string? value)
    {
        // 选中状态只在 Categories 上，选完必须同步书签条，否则高亮会停在「全部」
        UpdateCategorySelection();
        RefilterTools();
    }

    /// <summary>打开一个工具：进入工具台。</summary>
    public void OpenTool(ToolItemViewModel tool, bool fromRecent = false)
    {
        _detailFromRecent = fromRecent;
        SelectedTool = tool;
        IsDetailOpen = true;
        IsRecentOpen = false;
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
        SelectedCategory = name;
    }

    /// <summary>侧栏打开「最近使用」页。</summary>
    public void ShowRecent()
    {
        IsDetailOpen = false;
        IsRecentOpen = true;
    }

    /// <summary>侧栏回到工具墙。</summary>
    public void ShowTools()
    {
        IsDetailOpen = false;
        IsRecentOpen = false;
    }

    public bool HasRecentTools => RecentTools.Count > 0;

    /// <summary>输入框提示：优先显示拖入的图片，其次按工具输入形态给出说明。</summary>
    public string InputHint => IsImageInput && HasDroppedImage
        ? $"将使用拖入的图片：{_droppedImageName}"
        : SelectedTool?.Entry.Descriptor.Accepts switch
        {
            InputShape.Image => "将使用剪贴板里的图片",
            InputShape.None => "此工具不需要输入",
            _ => "输入内容…"
        };

    public bool IsTextInput => SelectedTool?.Entry.Descriptor.Accepts is InputShape.Text or InputShape.RichText;

    public bool IsImageInput => SelectedTool?.Entry.Descriptor.Accepts == InputShape.Image;

    /// <summary>只有当前是图片工具且确实拖入了图片，才显示「清除」。</summary>
    public bool CanClearDroppedImage => HasDroppedImage && IsImageInput;

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

    public bool IsTextResultVisible => !HasSvgResult;

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

    public string ExecuteLabel => IsExecuting ? "执行中…" : "执行";

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
        OnPropertyChanged(nameof(CanClearDroppedImage));
    }

    /// <summary>工具墙：先按侧栏分类，再按搜索词过滤，保持分组结构。</summary>
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
        OnPropertyChanged(nameof(EmptyToolMessage));
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
            category.IsSelected = !IsRecentOpen
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

        try
        {
            var context = new ToolContext
            {
                Input = input,
                Source = ToolSource.Clipboard,
                Parameters = parameters,
                Host = entry.Host
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

                    case TextPayload or OcrResultPayload:
                        var text = result.Payload switch
                        {
                            TextPayload textPayload => textPayload.Text,
                            OcrResultPayload ocrPayload => ocrPayload.Text,
                            _ => string.Empty
                        };

                        ShowTextResult(entry, context, text, elapsed);
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
            IsExecuting = false;
        }
    }

    private void ShowTextResult(ToolEntry entry, ToolContext context, string text, long elapsed)
    {
        ClearSvgResult();

        ResultText = text;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, text, elapsed);
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

    public void CopyResult()
    {
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
    /// 处理拖入的文件：图片进入图片输入，文本文件直接填进输入框。
    /// 只取第一个匹配项，不在这里做批量——批量是另一个产品的能力。
    /// </summary>
    public void AcceptDroppedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        var imagePath = paths.FirstOrDefault(path => ImageExtensions.Contains(Path.GetExtension(path)));
        if (imagePath is not null)
        {
            try
            {
                var info = new FileInfo(imagePath);
                if (info.Length > MaxDroppedImageBytes)
                {
                    ShowError("图片超过 20 MB，请先压缩");
                    return;
                }

                _droppedImagePng = File.ReadAllBytes(imagePath);
                _droppedImageFormat = FormatFromExtension(Path.GetExtension(imagePath));
                _droppedImageName = Path.GetFileName(imagePath);
                HasDroppedImage = true;

                // 当前不是图片工具时，自动切到第一个图片工具，省一次点击
                if (!IsImageInput)
                    OpenFirstMatching(InputShape.Image);

                return;
            }
            catch (Exception ex)
            {
                ShowError($"读取图片失败：{ex.Message}");
                return;
            }
        }

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

    public void ClearDroppedImage()
    {
        _droppedImagePng = null;
        _droppedImageName = null;
        HasDroppedImage = false;
    }

    private void OpenFirstMatching(InputShape shape)
    {
        var entry = _allTools.FirstOrDefault(tool => tool.Descriptor.Accepts == shape);
        if (entry is not null)
            OpenTool(new ToolItemViewModel(entry));
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

        if (descriptor.Accepts == InputShape.Image)
        {
            // 拖入的图片优先；没有才回退到剪贴板
            if (_droppedImagePng is not null)
                return new ImageInput(_droppedImagePng, _droppedImageFormat);

            var png = _clipboard.GetImagePng();
            if (png is null)
            {
                error = "剪贴板中没有图片。可以先复制图片，或直接把图片拖进窗口。";
                return null;
            }

            return new ImageInput(png, ImageFormat.Png);
        }

        return new TextInput(InputText);
    }

    private static string PreviewInput(IToolInput input) => input switch
    {
        TextInput text => text.Text.Length <= 120 ? text.Text : text.Text[..120] + "…",
        ImageInput image => $"图片 {image.ByteSize} 字节",
        _ => input.GetType().Name
    };

    private void ShowError(string message)
    {
        ClearSvgResult();
        ErrorText = message;
        HasError = true;
        HasResult = false;
        ResultText = null;
    }

    private void ClearResult()
    {
        ClearSvgResult();
        ResultText = null;
        ErrorText = null;
        HasResult = false;
        HasError = false;
    }

    private void ClearSvgResult()
    {
        SvgSource = null;
        SvgPreviewPng = null;
        SvgPreviewError = null;
        ShowSvgSource = false;
    }
}
