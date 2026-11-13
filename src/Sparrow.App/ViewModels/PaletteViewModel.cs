using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.History;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 命令面板的视图模型。
///
/// 只做三件事：搜索、执行、把结果整理成可展示的文本。
/// 不做的事：不碰窗口、不碰键盘、不碰插件加载——那些在 View 和 Registry 里。
/// </summary>
public partial class PaletteViewModel : ObservableObject
{
    // 结果区是只读展示，超长文本直接截断，避免 TextBlock 排版卡住 UI 线程
    private const int MaxDisplayLength = 20_000;

    private readonly Func<ToolRegistry> _registryFactory;
    private readonly IClipboardAccess _clipboard;
    private readonly IAppLog _log;
    private readonly IHistoryStore _history;
    private readonly ToolParameterStore? _parameters;

    private ToolRegistry? _registry;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private ToolItemViewModel? _selected;

    [ObservableProperty]
    private string? _resultText;

    /// <summary>结果区右上角的字数/行数；执行中或空结果时为空串。</summary>
    public string ResultSummary => IsPending ? string.Empty : TextMetrics.Summarize(ResultText);

    partial void OnResultTextChanged(string? value) => OnPropertyChanged(nameof(ResultSummary));

    partial void OnIsPendingChanged(bool value) => OnPropertyChanged(nameof(ResultSummary));

    /// <summary>结果区正在显示「正在识别…」之类的占位，不算真实结果。</summary>
    [ObservableProperty]
    private bool _isPending;

    [ObservableProperty]
    private string? _resultTitle;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>工具正在执行：显示顶部进度条，并阻止重复回车。</summary>
    [ObservableProperty]
    private bool _isExecuting;

    public ObservableCollection<ToolItemViewModel> Items { get; } = [];

    public PaletteViewModel(
        Func<ToolRegistry> registryFactory,
        IClipboardAccess clipboard,
        IAppLog log,
        IHistoryStore history,
        ToolParameterStore? parameters = null)
    {
        _registryFactory = registryFactory;
        _clipboard = clipboard;
        _log = log;
        _history = history;
        _parameters = parameters;

        // 刻意不在这里 Refresh：构造函数运行在启动路径上，
        // 而 Refresh 会触发插件扫描 + ALC 加载。首次搜索或 Reset 时才加载。
    }

    /// <summary>工具注册表。首次访问时构建，之后缓存。</summary>
    private ToolRegistry Registry => _registry ??= _registryFactory();

    partial void OnQueryChanged(string value) => Refresh();

    /// <summary>每次面板弹出时调用：清掉上一次的结果，保留查询词方便重复执行。</summary>
    public void Reset()
    {
        // 重新取一次注册表：插件可能在这期间被禁用或卸载
        _registry = _registryFactory();
        ClearResult();
        Refresh();
    }

    private void Refresh()
    {
        var startedAt = Stopwatch.GetTimestamp();
        var entries = Registry.Search(Query);

        Items.Clear();
        foreach (var entry in entries)
            Items.Add(new ToolItemViewModel(entry, Query));

        Selected = Items.FirstOrDefault();
        IsEmpty = Items.Count == 0;
        ClearResult();

        // 只记录有查询词的搜索；首次搜索包含插件加载，耗时会明显偏大
        if (!string.IsNullOrWhiteSpace(Query))
            _log.Info($"搜索「{Query}」：{entries.Count} 个结果，{Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} ms");
    }

    /// <summary>
    /// 执行当前选中的工具。
    /// 输入统一取自剪贴板——M0 的面板没有独立输入区，
    /// 「复制内容 → 热键 → 回车」是这个阶段唯一的工作流。
    /// </summary>
    public async Task ExecuteAsync()
    {
        if (Selected is null)
        {
            _log.Warn("执行被忽略：没有选中的工具");
            return;
        }

        if (IsExecuting)
        {
            _log.Warn("执行被忽略：上一个工具还在执行");
            return;
        }

        var entry = Selected.Entry;
        var tool = entry.Tool;

        IsExecuting = true;

        try
        {
            var (context, inputError) = BuildContext(entry);
            if (context is null)
            {
                _log.Warn($"无法构造输入：{inputError}");
                ShowError(inputError ?? "无法构造输入");
                return;
            }

            var startedAt = Stopwatch.GetTimestamp();

            _log.Info($"执行工具 {tool.Descriptor.Id}，输入类型 {context.Input.GetType().Name}");

            var result = await tool.ExecuteAsync(context, CancellationToken.None);

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            _log.Info($"工具 {tool.Descriptor.Id} 执行完成：成功={result.Success}，耗时={elapsed.TotalMilliseconds:F0}ms");

            if (result.Success && result.Payload is not null)
            {
                var text = FormatPayload(result.Payload);
                ShowResult(text, $"{tool.Descriptor.Name} · {elapsed.TotalMilliseconds:F0} ms");

                _history.Add(new HistoryEntry(
                    Guid.NewGuid().ToString("N"),
                    tool.Descriptor.Id,
                    tool.Descriptor.Name,
                    text,
                    PreviewInput(context.Input),
                    DateTimeOffset.Now,
                    (long)elapsed.TotalMilliseconds));

                return;
            }

            // Error.Detail 只进日志，界面上只显示 Message
            ShowError(result.Error?.Message ?? "执行失败，但没有返回错误信息");
        }
        catch (OperationCanceledException)
        {
            ShowError("已取消");
        }
        catch (Exception ex)
        {
            // 插件理论上不该抛异常，但宿主必须假设它会——这是「插件崩了壳不崩」的兜底
            _log.Error($"工具 {tool.Descriptor.Id} 执行异常", ex);
            ShowError($"工具执行异常：{ex.Message}");
        }
        finally
        {
            IsExecuting = false;
        }
    }

    /// <summary>宿主流程（截图识别等）直接展示结果。</summary>
    public void ShowExternalResult(string title, string text) => ShowResult(text, title);

    /// <summary>宿主流程直接展示错误。</summary>
    public void ShowExternalError(string message) => ShowError(message);

    /// <summary>执行中占位，避免用户以为没反应。</summary>
    public void ShowPending(string message)
    {
        IsPending = true;
        ResultText = message;
        ResultTitle = null;
        HasResult = true;
        HasError = false;
        ErrorText = null;
    }

    /// <summary>复制当前结果。没有结果时静默忽略。</summary>
    public void CopyResult()
    {
        if (string.IsNullOrEmpty(ResultText))
            return;

        if (_clipboard.SetText(ResultText))
        {
            ResultTitle = "已复制到剪贴板";
            _log.Info($"复制结果到剪贴板，长度 {ResultText.Length}");
        }
        else
        {
            // 复制失败必须让用户看见：多半是剪贴板被别的程序占着
            ResultTitle = "复制失败：剪贴板被占用，请重试";
            _log.Warn($"写入剪贴板失败，结果长度 {ResultText.Length}");
        }
    }

    private (ToolContext? Context, string? Error) BuildContext(ToolEntry entry)
    {
        var descriptor = entry.Descriptor;

        IToolInput input;
        ToolSource source;

        switch (descriptor.Accepts)
        {
            case InputShape.None:
                input = EmptyInput.Instance;
                source = ToolSource.Manual;
                break;

            case InputShape.Image:
                var png = _clipboard.GetImagePng();
                if (png is null)
                    return (null, "剪贴板里没有图片");

                input = new ImageInput(png, ImageFormat.Png);
                source = ToolSource.Clipboard;
                break;

            default:
                input = new TextInput(_clipboard.GetText() ?? string.Empty);
                source = ToolSource.Clipboard;
                break;
        }

        // 先取工具声明默认值，再用上次保存的值覆盖（例如截图常用的语言）
        var saved = _parameters?.Get(descriptor.Id);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in descriptor.Parameters)
        {
            if (saved is not null && saved.TryGetValue(parameter.Key, out var value))
                parameters[parameter.Key] = value;
            else if (parameter.Default is not null)
                parameters[parameter.Key] = parameter.Default;
        }

        return (new ToolContext
        {
            Input = input,
            Source = source,
            Parameters = parameters,
            Host = entry.Host
        }, null);
    }

    private static string PreviewInput(IToolInput input) => input switch
    {
        TextInput text => text.Text.Length <= 120 ? text.Text : text.Text[..120] + "…",
        ImageInput image => $"图片 {image.ByteSize} 字节",
        _ => input.GetType().Name
    };

    private static string FormatPayload(IToolPayload payload)
    {
        var text = payload switch
        {
            TextPayload textPayload => textPayload.Text,
            SvgPayload svgPayload => svgPayload.Svg,
            _ => $"（{payload.GetType().Name} 结果，面板暂不支持展示）"
        };

        return text.Length <= MaxDisplayLength
            ? text
            : text[..MaxDisplayLength] + Environment.NewLine + $"…… 已截断，仅显示前 {MaxDisplayLength} 个字符";
    }

    private void ShowResult(string text, string title)
    {
        IsPending = false;
        ResultText = text;
        ResultTitle = title;
        HasResult = true;
        HasError = false;
        ErrorText = null;
    }

    private void ShowError(string message)
    {
        IsPending = false;
        ErrorText = message;
        HasError = true;
        HasResult = false;
        ResultText = null;
        ResultTitle = null;
    }

    private void ClearResult()
    {
        IsPending = false;
        ResultText = null;
        ResultTitle = null;
        ErrorText = null;
        HasResult = false;
        HasError = false;
    }
}
