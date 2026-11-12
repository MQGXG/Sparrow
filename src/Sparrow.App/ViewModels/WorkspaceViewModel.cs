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

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    public ObservableCollection<HistoryEntryViewModel> Recent { get; } = [];

    private List<HistoryEntryViewModel> _allHistory = [];

    [ObservableProperty]
    private string _historyQuery = string.Empty;

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

            Groups.Clear();
            foreach (var group in entries
                         .GroupBy(entry => entry.Descriptor.Category)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                Groups.Add(new ToolGroupViewModel(group.Key, group));
            }

            SelectedTool = Groups.SelectMany(group => group.Tools).FirstOrDefault();
            InputText = _clipboard.GetText() ?? string.Empty;

            _allHistory = _history.Recent(200)
                .Select(entry => new HistoryEntryViewModel(entry))
                .ToList();

            FilterHistory();
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

    partial void OnSelectedToolChanged(ToolItemViewModel? value)
    {
        LoadParameters(value);
        OnPropertyChanged(nameof(InputHint));
        OnPropertyChanged(nameof(IsTextInput));
    }

    private void LoadParameters(ToolItemViewModel? tool)
    {
        Parameters.Clear();

        if (tool is null)
            return;

        // 参数控件完全由声明生成，插件不提供任何 UI
        foreach (var parameter in tool.Entry.Descriptor.Parameters)
            Parameters.Add(new ParameterViewModel(parameter));
    }

    public async Task ExecuteAsync()
    {
        if (SelectedTool is null)
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
