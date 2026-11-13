using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 设置页：插件管理 + 模型 + 热键 + 开机自启。
///
/// 工具自己的参数不在这里——它们在工具的参数区。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ToolCatalog _catalog;
    private readonly SettingsStore _store;
    private readonly StartupService _startup;
    private readonly AiClient? _ai;
    private readonly AiSecretStore? _secrets;
    private AppSettings _settings;

    public SettingsViewModel(
        ToolCatalog catalog,
        SettingsStore store,
        StartupService startup,
        AppSettings settings,
        AiClient? ai = null,
        AiSecretStore? secrets = null)
    {
        _catalog = catalog;
        _store = store;
        _startup = startup;
        _settings = settings;
        _ai = ai;
        _secrets = secrets;

        _searchHotkey = settings.SearchHotkey;
        _captureHotkey = settings.CaptureHotkey;
        _startWithWindows = startup.IsEnabled;
        _historyMaxEntries = settings.HistoryMaxEntries;
        _historyRetentionDays = settings.HistoryRetentionDays;

        _selectedAiProvider = AiProviders.Find(settings.AiProvider) ?? AiProviders.All[^1];
        _aiEnabled = settings.AiEnabled;
        _aiBaseUrl = settings.AiBaseUrl;
        _aiModel = settings.AiModel;
        _aiTemperature = settings.AiTemperature;
        _aiMaxTokens = settings.AiMaxTokens;

        Reload();
    }

    public IReadOnlyList<AiProviderPreset> AiProviderOptions => AiProviders.All;

    /// <summary>历史保留策略变化时通知宿主，让存储立即清理一次。</summary>
    public event Action<int, int>? HistoryRetentionChanged;

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public string VersionText { get; } =
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    [ObservableProperty]
    private string _searchHotkey = "Ctrl+Alt+S";

    [ObservableProperty]
    private string _captureHotkey = "Ctrl+Alt+C";

    [ObservableProperty]
    private bool _startWithWindows;

    /// <summary>历史最多保留条数；改完立即保存并清理。</summary>
    [ObservableProperty]
    private int _historyMaxEntries = 500;

    /// <summary>历史保留天数；0 表示不按天数清理。</summary>
    [ObservableProperty]
    private int _historyRetentionDays;

    /// <summary>当前 provider 预设；切换后自动填端点与默认模型。</summary>
    [ObservableProperty]
    private AiProviderPreset? _selectedAiProvider;

    [ObservableProperty]
    private bool _aiEnabled;

    [ObservableProperty]
    private string _aiBaseUrl = string.Empty;

    [ObservableProperty]
    private string _aiModel = string.Empty;

    /// <summary>只用于输入新密钥；已保存的密钥不会回显。</summary>
    [ObservableProperty]
    private string _aiKey = string.Empty;

    [ObservableProperty]
    private double _aiTemperature = 0.2;

    [ObservableProperty]
    private int _aiMaxTokens = 2048;

    [ObservableProperty]
    private string? _aiStatus;

    [ObservableProperty]
    private string? _statusMessage;

    public void Reload()
    {
        Plugins.Clear();

        foreach (var candidate in _catalog.ScanCandidates())
        {
            var manifest = candidate.Manifest;
            if (manifest is null)
                continue;

            var enabled = !_settings.DisabledPlugins.Contains(manifest.Id, StringComparer.Ordinal);

            Plugins.Add(new PluginItemViewModel(
                manifest.Id,
                manifest.Name,
                manifest.Version,
                manifest.Tools.Length,
                enabled)
            {
                Changed = Persist
            });
        }
    }

    /// <summary>解析并保存热键。重启生效——重建热键需要重启消息循环，M1 不做运行时热切换。</summary>
    public void SaveHotkeys()
    {
        if (!HotkeyText.TryParse(SearchHotkey, out var searchModifiers, out var searchKey)
            || !HotkeyText.TryParse(CaptureHotkey, out var captureModifiers, out var captureKey))
        {
            StatusMessage = "热键格式错误，示例：Ctrl+Alt+S";
            return;
        }

        _settings = _settings with
        {
            SearchHotkey = HotkeyText.Format(searchModifiers, searchKey),
            CaptureHotkey = HotkeyText.Format(captureModifiers, captureKey)
        };

        _store.Save(_settings);
        StatusMessage = "已保存，重启应用生效";
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        var ok = value ? _startup.Enable() : _startup.Disable();
        StatusMessage = ok
            ? (value ? "已启用开机自启" : "已关闭开机自启")
            : "修改开机自启失败，详情见日志";
    }

    partial void OnHistoryMaxEntriesChanged(int value) => PersistHistory();

    partial void OnHistoryRetentionDaysChanged(int value) => PersistHistory();

    /// <summary>
    /// 保存历史保留策略。XAML 初始化绑定时可能先写一次默认值，
    /// 和已保存值相同就直接跳过，避免启动时无谓地重写 settings.json。
    /// </summary>
    private void PersistHistory()
    {
        if (HistoryMaxEntries == _settings.HistoryMaxEntries
            && HistoryRetentionDays == _settings.HistoryRetentionDays)
            return;

        _settings = _settings with
        {
            HistoryMaxEntries = HistoryMaxEntries,
            HistoryRetentionDays = HistoryRetentionDays
        };

        _store.Save(_settings);
        HistoryRetentionChanged?.Invoke(HistoryMaxEntries, HistoryRetentionDays);
        StatusMessage = "历史保留策略已保存";
    }

    partial void OnSelectedAiProviderChanged(AiProviderPreset? value)
    {
        if (value is null)
            return;

        // 「自定义」保留用户已填的端点，不覆盖
        if (value.Key == "custom")
            return;

        // 只在当前端点/模型仍是某个预设默认值时套用新预设，
        // 避免用户在设置页加载时把自定义端点覆盖掉
        var currentIsPresetDefault = AiProviders.All.Any(provider =>
            provider.Key != "custom"
            && string.Equals(provider.BaseUrl, AiBaseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(provider.Model, AiModel, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(AiBaseUrl) && !currentIsPresetDefault)
            return;

        // 用户主动切换 provider 时，带出该家的默认端点与模型
        AiBaseUrl = value.BaseUrl;
        AiModel = value.Model;
    }

    /// <summary>保存模型配置；密钥单独加密保存，留空表示不修改。</summary>
    public void SaveAiSettings()
    {
        _settings = _settings with
        {
            AiEnabled = AiEnabled,
            AiProvider = SelectedAiProvider?.Key ?? "custom",
            AiBaseUrl = AiBaseUrl.Trim(),
            AiModel = AiModel.Trim(),
            AiTemperature = AiTemperature,
            AiMaxTokens = AiMaxTokens
        };

        _store.Save(_settings);

        if (!string.IsNullOrWhiteSpace(AiKey))
        {
            _secrets?.Save(AiKey);
            AiKey = string.Empty;
        }

        _ai?.Configure(_settings);
        AiStatus = _ai?.IsAvailable == true
            ? "模型配置已保存"
            : "已保存，但还缺少 API Key 或有效端点";
    }

    /// <summary>用当前配置发一条最小请求，验证端点、密钥和模型名。</summary>
    public async Task TestAiAsync()
    {
        if (_ai is null)
        {
            AiStatus = "模型模块未初始化";
            return;
        }

        SaveAiSettings();

        if (!_ai.IsAvailable)
        {
            AiStatus = "还缺少 API Key 或有效端点";
            return;
        }

        AiStatus = "正在测试…";

        try
        {
            var response = await _ai.CompleteAsync(
                new AiRequest
                {
                    Messages = [new AiMessage(AiRole.User, "只回复单词：pong")],
                    Temperature = 0,
                    MaxTokens = 16
                },
                CancellationToken.None);

            AiStatus = $"连接成功：{response.Text}";
        }
        catch (AiException ex)
        {
            AiStatus = ex.Message;
        }
        catch (Exception ex)
        {
            AiStatus = $"测试失败：{ex.Message}";
        }
    }

    public void ClearAiKey()
    {
        _secrets?.Save(null);
        _ai?.Configure(_settings);
        AiStatus = "API Key 已清除";
    }

    /// <summary>
    /// 重新扫描插件目录。放在后台线程，避免加载 OCR 时卡住设置页。
    /// </summary>
    public async Task RescanAsync()
    {
        StatusMessage = "正在重新扫描…";

        try
        {
            await Task.Run(() =>
            {
                _catalog.Reload();
                _catalog.GetRegistry();
            });

            Reload();
            StatusMessage = $"已重新扫描：{Plugins.Count} 个插件";
        }
        catch (Exception ex)
        {
            StatusMessage = $"重新扫描失败：{ex.Message}";
        }
    }

    private void Persist()
    {
        var disabled = Plugins
            .Where(plugin => !plugin.IsEnabled)
            .Select(plugin => plugin.Id)
            .ToArray();

        _settings = _settings with { DisabledPlugins = disabled };
        _store.Save(_settings);
        StatusMessage = "插件设置已保存，重启应用生效";
    }
}
