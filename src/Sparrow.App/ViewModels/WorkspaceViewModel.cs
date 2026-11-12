using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public WorkspaceViewModel(
        ToolCatalog catalog,
        IClipboardAccess clipboard,
        IAppLog log,
        SettingsViewModel settings)
    {
        _catalog = catalog;
        _clipboard = clipboard;
        _log = log;
        Settings = settings;
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

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

    /// <summary>每次打开工作区时刷新工具列表与剪贴板输入。</summary>
    public void Reload()
    {
        var registry = _catalog.GetRegistry();

        Groups.Clear();
        foreach (var group in registry.Search(null, int.MaxValue)
                     .GroupBy(entry => entry.Descriptor.Category)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            Groups.Add(new ToolGroupViewModel(group.Key, group));
        }

        SelectedTool = Groups.SelectMany(group => group.Tools).FirstOrDefault();
        InputText = _clipboard.GetText() ?? string.Empty;
        ClearResult();
    }

    partial void OnSelectedToolChanged(ToolItemViewModel? value) => LoadParameters(value);

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
