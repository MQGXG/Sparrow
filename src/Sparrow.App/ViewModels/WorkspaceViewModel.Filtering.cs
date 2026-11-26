// WorkspaceViewModel 分部类：历史筛选、刷新、分类重建与参数持久化。
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

public partial class WorkspaceViewModel
{
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

    /// <summary>当前执行的取消源；没有执行时为 null。</summary>
    private CancellationTokenSource? _executionCts;

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

        // 全局序号：卡片右上角 01..N，墙面像一份可检索的目录
        for (var i = 0; i < tools.Count; i++)
            tools[i].Index = i + 1;

        Groups.Clear();
        VisibleTools.Clear();

        foreach (var tool in tools)
            VisibleTools.Add(tool);

        var orderedGroups = tools
            .GroupBy(tool => tool.CategoryLabel)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < orderedGroups.Count; i++)
            Groups.Add(new ToolGroupViewModel(orderedGroups[i].Key, orderedGroups[i], i + 1));

        OnPropertyChanged(nameof(ToolCount));
        HasTools = list.Count > 0;        UpdateGroupExpansion();
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
}
