using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.History;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
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

    public WorkspaceViewModel(
        ToolCatalog catalog,
        IClipboardAccess clipboard,
        IAppLog log,
        IHistoryStore history,
        SettingsViewModel settings)
    {
        _catalog = catalog;
        _clipboard = clipboard;
        _log = log;
        _history = history;
        Settings = settings;
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

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

    /// <summary>工作区双态：false = 工具墙（启动器），true = 工具台（输入/结果详情）。</summary>
    [ObservableProperty]
    private bool _isDetailOpen;

    partial void OnToolQueryChanged(string value) => RefilterTools();

    partial void OnSelectedCategoryChanged(string? value)
    {
        // 选中状态只在 Categories 上，选完必须同步书签条，否则高亮会停在「全部」
        UpdateCategorySelection();
        RefilterTools();
    }

    /// <summary>打开一个工具：进入工具台。</summary>
    public void OpenTool(ToolItemViewModel tool)
    {
        SelectedTool = tool;
        IsDetailOpen = true;
    }

    /// <summary>返回工具墙。保留选中项，方便「返回再进来」。</summary>
    public void CloseDetail() => IsDetailOpen = false;

    /// <summary>侧栏点分类；name 为 null 表示「全部」。</summary>
    public void SelectCategory(string? name) => SelectedCategory = name;

    public bool HasRecentTools => RecentTools.Count > 0;

    /// <summary>输入框提示：图片工具用剪贴板图片，生成类工具不需要输入。</summary>
    public string InputHint => SelectedTool?.Entry.Descriptor.Accepts switch
    {
        InputShape.Image => "将使用剪贴板里的图片",
        InputShape.None => "此工具不需要输入",
        _ => "输入内容…"
    };

    public bool IsTextInput => SelectedTool?.Entry.Descriptor.Accepts is InputShape.Text or InputShape.RichText;

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

    [ObservableProperty]
    private string? _resultText;

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
        if (oldValue is not null)
            oldValue.IsSelected = false;

        if (newValue is not null)
            newValue.IsSelected = true;

        LoadParameters(newValue);
        OnPropertyChanged(nameof(InputHint));
        OnPropertyChanged(nameof(IsTextInput));
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

        Groups.Clear();
        foreach (var group in list
                     .GroupBy(entry => entry.Descriptor.Category)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Groups.Add(new ToolGroupViewModel(group.Key, group));
        }

        HasTools = list.Count > 0;
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
            category.IsSelected = SelectedCategory is null
                ? category.Name == "全部"
                : category.Name == SelectedCategory;
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

        // 参数控件完全由声明生成，插件不提供任何 UI
        foreach (var parameter in tool.Entry.Descriptor.Parameters)
            Parameters.Add(new ParameterViewModel(parameter));

        OnPropertyChanged(nameof(HasParameters));
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

            var result = await entry.Tool.ExecuteAsync(context, CancellationToken.None);

            if (result.Success && result.Payload is TextPayload text)
            {
                ResultText = text.Text;
                HasResult = true;
                HasError = false;
                ErrorText = null;

                var historyEntry = new HistoryEntry(
                    Guid.NewGuid().ToString("N"),
                    entry.Descriptor.Id,
                    entry.Descriptor.Name,
                    text.Text,
                    PreviewInput(context.Input),
                    DateTimeOffset.Now,
                    0);

                _history.Add(historyEntry);
                _allHistory.Insert(0, new HistoryEntryViewModel(historyEntry));
                FilterHistory();
                RebuildRecentTools();
                return;
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
        OnPropertyChanged(nameof(HasRecentTools));
    }

    public void ShowSettings() => IsSettingsVisible = true;

    public void HideSettings() => IsSettingsVisible = false;

    private IToolInput? BuildInput(ToolDescriptor descriptor, out string? error)
    {
        error = null;

        if (descriptor.Accepts == InputShape.None)
            return EmptyInput.Instance;

        if (descriptor.Accepts == InputShape.Image)
        {
            var png = _clipboard.GetImagePng();
            if (png is null)
            {
                error = "剪贴板里没有图片";
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
        ErrorText = message;
        HasError = true;
        HasResult = false;
        ResultText = null;
    }

    private void ClearResult()
    {
        ResultText = null;
        ErrorText = null;
        HasResult = false;
        HasError = false;
    }
}
