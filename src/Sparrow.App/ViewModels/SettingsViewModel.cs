using System.Collections.ObjectModel;
using Avalonia;
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
public partial class SettingsViewModel : ObservableObject, IClipboardExclusionStore
{
    private readonly ToolCatalog _catalog;
    private readonly SettingsStore _store;
    private readonly StartupService _startup;
    private readonly AiClient? _ai;
    private readonly AiSecretStore? _secrets;
    private readonly ILocalModels? _localModels;
    private AppSettings _settings;

    public SettingsViewModel(
        ToolCatalog catalog,
        SettingsStore store,
        StartupService startup,
        AppSettings settings,
        AiClient? ai = null,
        AiSecretStore? secrets = null,
        ILocalModels? localModels = null)
    {
        _catalog = catalog;
        _store = store;
        _startup = startup;
        _settings = settings;
        _appearance = ThemeService.Normalize(settings.Appearance);
        _ai = ai;
        _secrets = secrets;
        _localModels = localModels;

        _searchHotkey = settings.SearchHotkey;
        _captureHotkey = settings.CaptureHotkey;
        _recordHotkey = settings.RecordHotkey;
        _clipboardPanelHotkey = settings.ClipboardPanelHotkey;
        _captureRecognizeKey = settings.CaptureRecognizeKey;
        _captureTranslateKey = settings.CaptureTranslateKey;
        _captureCopyKey = settings.CaptureCopyKey;
        _captureSaveKey = settings.CaptureSaveKey;
        _capturePinKey = settings.CapturePinKey;
        _captureScrollKey = settings.CaptureScrollKey;
        _captureColorKey = settings.CaptureColorKey;
        _captureOcrBoxKey = settings.CaptureOcrBoxKey;
        _selectedCaptureElementApi = CaptureElementApiOptions
            .FirstOrDefault(option => option.Key == settings.CaptureElementApi)
            ?? CaptureElementApiOptions[0];
        _selectedCaptureApi = CaptureApiOptions
            .FirstOrDefault(option => option.Key == settings.CaptureApi)
            ?? CaptureApiOptions[0];
        _captureShowCursor = settings.CaptureShowCursor;
        _captureUiInScroll = settings.CaptureUiInScroll;
        _doubleCtrlSearchEnabled = settings.DoubleCtrlSearchEnabled;
        _captureRestoreColors = settings.CaptureRestoreColors;

        _selectionBorderColor = SelectionBorderColorOptions
            .FirstOrDefault(option => option.Key == settings.SelectionBorderColor)
            ?? SelectionBorderColorOptions[0];
        _dataDirectory = settings.DataDirectory;
        _modelDirectory = settings.ModelDirectory;
        _recordDirectory = settings.RecordDirectory;
        _startWithWindows = startup.IsEnabled;
        _historyMaxEntries = settings.HistoryMaxEntries;
        _historyRetentionDays = settings.HistoryRetentionDays;
        _clipboardHistoryEnabled = settings.ClipboardHistoryEnabled;
        _clipboardRetentionDays = settings.ClipboardHistoryRetentionDays;
        _clipboardMaxMb = Math.Max(0, settings.ClipboardHistoryMaxBytes / (1024 * 1024));

        _selectedAiProvider = AiProviders.Find(settings.AiProvider) ?? AiProviders.All[^1];
        _aiEnabled = settings.AiEnabled;
        _aiBaseUrl = settings.AiBaseUrl;
        _aiModel = settings.AiModel;
        _aiTemperature = settings.AiTemperature;
        _aiMaxTokens = settings.AiMaxTokens;
        _localModelMirror = settings.LocalModelMirror;
        _updateFeedUrl = settings.UpdateFeedUrl;

        ReloadLocalModels();
        Reload();
    }

    private bool _toolHotkeysLoaded;

    public IReadOnlyList<AiProviderPreset> AiProviderOptions => AiProviders.All;

    /// <summary>历史保留策略变化时通知宿主，让存储立即清理一次。</summary>
    public event Action<int, int>? HistoryRetentionChanged;

    /// <summary>剪贴板历史开关变化时通知宿主，让监听立即暂停 / 恢复。</summary>
    public event Action<bool>? ClipboardHistoryEnabledChanged;

    /// <summary>剪贴板保留策略变化时通知宿主，让存储立即清理一次。</summary>
    public event Action<int, long>? ClipboardRetentionChanged;

    /// <summary>「不记录」名单变化时通知监听器。</summary>
    public event Action<IReadOnlyList<string>>? ClipboardExclusionsChanged;

    /// <summary>外观三态变化时通知宿主即时换肤（设置页与顶栏快捷按钮共用）。</summary>
    public event Action<string>? AppearanceChanged;

    /// <summary>双击 Ctrl 文件搜索开关变化时通知宿主，立即安装/卸载全局钩子。</summary>
    public event Action<bool>? DoubleCtrlSearchEnabledChanged;

    private string _appearance;

    /// <summary>system / light / dark；写入即持久化并通知宿主。</summary>
    public string Appearance
    {
        get => _appearance;
        set
        {
            var next = ThemeService.Normalize(value);
            if (!SetProperty(ref _appearance, next))
                return;

            _settings = _settings with { Appearance = next };
            _store.Save(_settings);
            OnPropertyChanged(nameof(IsSystemAppearance));
            OnPropertyChanged(nameof(IsLightAppearance));
            OnPropertyChanged(nameof(IsDarkAppearance));
            AppearanceChanged?.Invoke(next);
        }
    }

    public bool IsSystemAppearance => _appearance == ThemeService.System;
    public bool IsLightAppearance => _appearance == ThemeService.Light;
    public bool IsDarkAppearance => _appearance == ThemeService.Dark;

    /// <summary>顶栏快捷按钮：在当前观感的深 / 浅之间互切。</summary>
    public void ToggleDarkLight() =>
        Appearance = ThemeService.Toggle(_appearance, Application.Current?.ActualThemeVariant);

    /// <summary>剪贴板保留天数；0 = 永久。</summary>
    [ObservableProperty]
    private int _clipboardRetentionDays;

    /// <summary>剪贴板总容量上限（MB）。</summary>
    [ObservableProperty]
    private long _clipboardMaxMb;

    /// <summary>剪贴板页通过这个接口改排除名单。</summary>
    public IReadOnlyList<string> ExcludedApps => _settings.ClipboardExcludedApps;

    public void SaveExcludedApps(IReadOnlyList<string> apps)
    {
        var next = new List<string>(apps);

        if (next.SequenceEqual(_settings.ClipboardExcludedApps, StringComparer.OrdinalIgnoreCase))
            return;

        _settings = _settings with { ClipboardExcludedApps = next };
        _store.Save(_settings);
        ClipboardExclusionsChanged?.Invoke(next);
    }

    /// <summary>截图 OCR 的识别框模式；工具栏切换后立即落盘。</summary>
    public OcrBoxMode CaptureOcrBoxMode => OcrBoxModes.Parse(_settings.CaptureOcrBoxMode);

    public void SetCaptureOcrBoxMode(string mode)
    {
        if (string.Equals(_settings.CaptureOcrBoxMode, mode, StringComparison.Ordinal))
            return;

        _settings = _settings with { CaptureOcrBoxMode = mode };
        _store.Save(_settings);
    }

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    /// <summary>离线模型清单；宿主没注入时为空（测试或精简宿主）。</summary>
    public ObservableCollection<LocalModelItemViewModel> LocalModels { get; } = [];

    public bool HasLocalModels => LocalModels.Count > 0;

    /// <summary>设置页「模型」分段的离线模型列表。</summary>
    private void ReloadLocalModels()
    {
        LocalModels.Clear();

        if (_localModels is null)
            return;

        foreach (var info in _localModels.All)
        {
            LocalModels.Add(new LocalModelItemViewModel(
                info,
                _localModels,
                message => StatusMessage = message));
        }

        OnPropertyChanged(nameof(HasLocalModels));
    }

    /// <summary>工具热键编辑行：工具全集 + 已配置的热键。空文本 = 不绑定。</summary>
    public ObservableCollection<ToolHotkeyRowViewModel> ToolHotkeys { get; } = [];

    public bool HasToolHotkeys => ToolHotkeys.Count > 0;

    public string VersionText { get; } =
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    [ObservableProperty]
    private string _searchHotkey = "Ctrl+Alt+S";

    [ObservableProperty]
    private string _captureHotkey = "Ctrl+Alt+C";

    [ObservableProperty]
    private string _recordHotkey = "Ctrl+Alt+Shift+R";

    /// <summary>截图窗口内的动作键；空字符串表示禁用该快捷键。</summary>
    [ObservableProperty]
    private string _captureRecognizeKey = "R";

    [ObservableProperty]
    private string _captureTranslateKey = "T";

    [ObservableProperty]
    private string _captureCopyKey = "C";

    [ObservableProperty]
    private string _captureSaveKey = "S";

    [ObservableProperty]
    private string _capturePinKey = "P";

    [ObservableProperty]
    private string _captureScrollKey = "G";

    [ObservableProperty]
    private string _captureColorKey = "I";

    [ObservableProperty]
    private string _captureOcrBoxKey = "B";

    /// <summary>窗口元素 API：auto（UIA 优先 + MSAA 兜底）/ uia / msaa。</summary>
    [ObservableProperty]
    private SettingsChoiceOption _selectedCaptureElementApi = null!;

    public IReadOnlyList<SettingsChoiceOption> CaptureElementApiOptions { get; } =
    [
        new("auto", "自动（UI Automation，推荐）"),
        new("uia", "仅 UI Automation"),
        new("msaa", "仅 MSAA（旧应用）")
    ];

    partial void OnSelectedCaptureElementApiChanged(SettingsChoiceOption value)
    {
        _settings = _settings with { CaptureElementApi = value.Key };
        _store.Save(_settings);
        StatusMessage = $"窗口元素 API：{value.Label}";
    }

    /// <summary>截图取帧 API：auto（WGC 优先 / GDI 兜底）/ wgc / gdi。</summary>
    [ObservableProperty]
    private SettingsChoiceOption _selectedCaptureApi = null!;

    public IReadOnlyList<SettingsChoiceOption> CaptureApiOptions { get; } =
    [
        new("auto", "自动（平时 GDI，HDR 用 WGC）"),
        new("wgc", "Windows Graphics Capture"),
        new("gdi", "GDI")
    ];

    partial void OnSelectedCaptureApiChanged(SettingsChoiceOption value)
    {
        _settings = _settings with { CaptureApi = value.Key };
        _store.Save(_settings);
        ScreenCapture.Api = value.Key switch
        {
            "gdi" => CaptureApi.Gdi,
            "wgc" => CaptureApi.Wgc,
            _ => CaptureApi.Auto
        };

        StatusMessage = $"截图捕获 API：{value.Label}";
    }

    [ObservableProperty]
    private bool _captureShowCursor;

    partial void OnCaptureShowCursorChanged(bool value)
    {
        _settings = _settings with { CaptureShowCursor = value };
        _store.Save(_settings);
        ScreenCapture.ShowCursor = value;
    }

    [ObservableProperty]
    private bool _captureUiInScroll = true;

    partial void OnCaptureUiInScrollChanged(bool value)
    {
        _settings = _settings with { CaptureUiInScroll = value };
        _store.Save(_settings);
    }

    [ObservableProperty]
    private bool _doubleCtrlSearchEnabled = true;

    partial void OnDoubleCtrlSearchEnabledChanged(bool value)
    {
        _settings = _settings with { DoubleCtrlSearchEnabled = value };
        _store.Save(_settings);
        DoubleCtrlSearchEnabledChanged?.Invoke(value);
    }

    [ObservableProperty]
    private bool _captureRestoreColors = true;

    partial void OnCaptureRestoreColorsChanged(bool value)
    {
        _settings = _settings with { CaptureRestoreColors = value };
        _store.Save(_settings);
        ScreenCapture.RestoreColors = value;
    }

    /// <summary>选区边框颜色；同时用于十字辅助线和悬停候选框。</summary>
    [ObservableProperty]
    private SettingsChoiceOption _selectionBorderColor = null!;

    public IReadOnlyList<SettingsChoiceOption> SelectionBorderColorOptions { get; } =
    [
        new("#E5A44C", "琥珀（默认）"),
        new("#4C9AFF", "蓝色"),
        new("#3DD68C", "绿色"),
        new("#FF6B6B", "红色"),
        new("#FFFFFF", "白色"),
        new("#22D3EE", "青色"),
        new("#C084FC", "紫色")
    ];

    partial void OnSelectionBorderColorChanged(SettingsChoiceOption value)
    {
        _settings = _settings with { SelectionBorderColor = value.Key };
        _store.Save(_settings);
        StatusMessage = $"选区边框颜色：{value.Label}";
    }

    /// <summary>数据目录：剪贴板/模型/录制/历史；空 = 默认的 %LOCALAPPDATA%\Sparrow。</summary>
    [ObservableProperty]
    private string _dataDirectory = string.Empty;

    public string DataDirectoryDisplay =>
        string.IsNullOrWhiteSpace(DataDirectory) ? SparrowPaths.ConfigRoot : DataDirectory;

    partial void OnDataDirectoryChanged(string value)
    {
        _settings = _settings with { DataDirectory = value };
        _store.Save(_settings);
        OnPropertyChanged(nameof(DataDirectoryDisplay));
        OnPropertyChanged(nameof(ModelDirectoryDisplay));
        OnPropertyChanged(nameof(RecordDirectoryDisplay));
        StatusMessage = "数据目录已更改，重启后生效";
    }

    /// <summary>离线模型目录；空 = 数据目录下的 models。</summary>
    [ObservableProperty]
    private string _modelDirectory = string.Empty;

    public string ModelDirectoryDisplay =>
        string.IsNullOrWhiteSpace(ModelDirectory)
            ? Path.Combine(DataDirectoryDisplay, "models")
            : ModelDirectory;

    partial void OnModelDirectoryChanged(string value)
    {
        _settings = _settings with { ModelDirectory = value };
        _store.Save(_settings);
        OnPropertyChanged(nameof(ModelDirectoryDisplay));
        StatusMessage = "模型目录已更改，重启后生效";
    }

    /// <summary>录制文件目录；空 = 数据目录下的 recordings。</summary>
    [ObservableProperty]
    private string _recordDirectory = string.Empty;

    public string RecordDirectoryDisplay =>
        string.IsNullOrWhiteSpace(RecordDirectory)
            ? Path.Combine(DataDirectoryDisplay, "recordings")
            : RecordDirectory;

    partial void OnRecordDirectoryChanged(string value)
    {
        _settings = _settings with { RecordDirectory = value };
        _store.Save(_settings);
        OnPropertyChanged(nameof(RecordDirectoryDisplay));
        StatusMessage = "录制目录已更改，重启后生效";
    }

    /// <summary>日志目录固定：启动早期就要写日志，不能依赖设置。</summary>
    public string LogDirectoryDisplay => SparrowPaths.Logs;

    [ObservableProperty]
    private string _clipboardPanelHotkey = "Ctrl+Alt+B";

    [ObservableProperty]
    private bool _startWithWindows;

    /// <summary>历史最多保留条数；改完立即保存并清理。</summary>
    [ObservableProperty]
    private int _historyMaxEntries = 500;

    /// <summary>历史保留天数；0 表示不按天数清理。</summary>
    [ObservableProperty]
    private int _historyRetentionDays;

    /// <summary>是否记录剪贴板历史。改完立即生效：监听暂停 / 恢复，已记录的内容保留。</summary>
    [ObservableProperty]
    private bool _clipboardHistoryEnabled = true;

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

    /// <summary>离线模型的自托管镜像根地址；空表示只用内置源。</summary>
    [ObservableProperty]
    private string _localModelMirror = string.Empty;

    /// <summary>Velopack 更新源地址。</summary>
    [ObservableProperty]
    private string _updateFeedUrl = string.Empty;

    [ObservableProperty]
    private string? _updateStatus;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    public bool IsNotCheckingUpdate => !IsCheckingUpdate;

    partial void OnIsCheckingUpdateChanged(bool value) =>
        OnPropertyChanged(nameof(IsNotCheckingUpdate));

    /// <summary>当前设置分段；默认插件页，和进入设置的习惯一致。</summary>
    [ObservableProperty]
    private SettingsSection _selectedSection = SettingsSection.Plugins;

    public bool IsPluginsSelected => SelectedSection == SettingsSection.Plugins;

    public bool IsModelSelected => SelectedSection == SettingsSection.Model;

    public bool IsHotkeysSelected => SelectedSection == SettingsSection.Hotkeys;

    public bool IsGeneralSelected => SelectedSection == SettingsSection.General;

    public bool IsDataSelected => SelectedSection == SettingsSection.Data;

    public bool IsAboutSelected => SelectedSection == SettingsSection.About;

    /// <summary>全部插件注册的工具总数（关于页指标用）。</summary>
    public int ToolCount => Plugins.Sum(plugin => plugin.ToolCount);

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsPluginsSelected));
        OnPropertyChanged(nameof(IsModelSelected));
        OnPropertyChanged(nameof(IsHotkeysSelected));
        OnPropertyChanged(nameof(IsGeneralSelected));
        OnPropertyChanged(nameof(IsDataSelected));
        OnPropertyChanged(nameof(IsAboutSelected));

        // 工具热键列表才需要工具注册表：切到热键分段时才加载插件，冷启动路径保持零加载
        if (value == SettingsSection.Hotkeys && !_toolHotkeysLoaded)
            ReloadToolHotkeys();
    }

    public void SelectSection(SettingsSection section) => SelectedSection = section;

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

        // 插件列表变了（重新扫描后），工具热键行需要跟着重建；没显示过热键分段就不碰注册表
        if (_toolHotkeysLoaded)
            ReloadToolHotkeys();

        OnPropertyChanged(nameof(ToolCount));
    }

    /// <summary>工具热键行：工具全集（注册表）按名称排序，预填已配置值。</summary>
    private void ReloadToolHotkeys()
    {
        ToolHotkeys.Clear();
        _toolHotkeysLoaded = true;

        try
        {
            var entries = _catalog.GetRegistry().Search(null, int.MaxValue);
            foreach (var entry in entries.OrderBy(entry => entry.Descriptor.Name, StringComparer.Ordinal))
            {
                var id = entry.Descriptor.Id;
                var configured = _settings.ToolHotkeys.TryGetValue(id, out var hotkey)
                    ? hotkey
                    : string.Empty;

                ToolHotkeys.Add(new ToolHotkeyRowViewModel(id, entry.Descriptor.Name, configured));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载工具列表失败：{ex.Message}";
        }

        OnPropertyChanged(nameof(HasToolHotkeys));
    }

    /// <summary>解析并保存热键。重启生效——重建热键需要重启消息循环，M1 不做运行时热切换。</summary>
    public void SaveHotkeys()
    {
        if (!HotkeyText.TryParse(SearchHotkey, out var searchModifiers, out var searchKey)
            || !HotkeyText.TryParse(CaptureHotkey, out var captureModifiers, out var captureKey)
            || !HotkeyText.TryParse(RecordHotkey, out var recordModifiers, out var recordKey)
            || !HotkeyText.TryParse(ClipboardPanelHotkey, out var panelModifiers, out var panelKey))
        {
            StatusMessage = "热键格式错误，示例：Ctrl+Alt+S";
            return;
        }

        var searchText = HotkeyText.Format(searchModifiers, searchKey);
        var captureText = HotkeyText.Format(captureModifiers, captureKey);
        var recordText = HotkeyText.Format(recordModifiers, recordKey);
        var panelText = HotkeyText.Format(panelModifiers, panelKey);

        // 同一组合被注册两次时 RegisterHotKey 会静默失败，这里先拦下来
        if (string.Equals(searchText, captureText, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"热键重复：{captureText}";
            return;
        }

        if (string.Equals(panelText, searchText, StringComparison.OrdinalIgnoreCase)
            || string.Equals(panelText, captureText, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"热键重复：{panelText}";
            return;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            searchText,
            captureText,
            recordText,
            panelText
        };

        var recognizeKey = string.Empty;
        var translateKey = string.Empty;
        var copyKey = string.Empty;
        var saveKey = string.Empty;
        var pinKey = string.Empty;
        var scrollKey = string.Empty;
        var colorKey = string.Empty;
        var ocrBoxKey = string.Empty;

        if (!TryNormalizeCaptureKey(CaptureRecognizeKey, out recognizeKey)
            || !TryNormalizeCaptureKey(CaptureTranslateKey, out translateKey)
            || !TryNormalizeCaptureKey(CaptureCopyKey, out copyKey)
            || !TryNormalizeCaptureKey(CaptureSaveKey, out saveKey)
            || !TryNormalizeCaptureKey(CapturePinKey, out pinKey)
            || !TryNormalizeCaptureKey(CaptureScrollKey, out scrollKey)
            || !TryNormalizeCaptureKey(CaptureColorKey, out colorKey)
            || !TryNormalizeCaptureKey(CaptureOcrBoxKey, out ocrBoxKey))
        {
            StatusMessage = "截图窗口快捷键格式错误，示例：R 或 Ctrl+C；留空表示禁用";
            return;
        }

        var toolHotkeys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in ToolHotkeys)
        {
            var text = row.HotkeyText.Trim();
            if (text.Length == 0)
                continue;

            if (!HotkeyText.TryParse(text, out var modifiers, out var key))
            {
                StatusMessage = $"「{row.Name}」的热键格式错误，示例：Ctrl+Alt+P";
                return;
            }

            var formatted = HotkeyText.Format(modifiers, key);
            if (!used.Add(formatted))
            {
                StatusMessage = $"热键重复：{formatted}";
                return;
            }

            toolHotkeys[row.ToolId] = formatted;
        }

        _settings = _settings with
        {
            SearchHotkey = searchText,
            CaptureHotkey = captureText,
            RecordHotkey = recordText,
            ClipboardPanelHotkey = panelText,
            CaptureRecognizeKey = recognizeKey,
            CaptureTranslateKey = translateKey,
            CaptureCopyKey = copyKey,
            CaptureSaveKey = saveKey,
            CapturePinKey = pinKey,
            CaptureScrollKey = scrollKey,
            CaptureColorKey = colorKey,
            CaptureOcrBoxKey = ocrBoxKey,
            ToolHotkeys = toolHotkeys
        };

        _store.Save(_settings);
        StatusMessage = "已保存，截图窗口快捷键立即生效，全局热键重启后生效";
    }

    /// <summary>校验并规范化截图窗口内的动作键；空字符串 = 禁用。</summary>
    private static bool TryNormalizeCaptureKey(string? text, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!HotkeyText.TryParse(text, out var modifiers, out var virtualKey))
            return false;

        normalized = HotkeyText.Format(modifiers, virtualKey);
        return true;
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

    /// <summary>剪贴板历史开关：立即生效并落盘，已记录的内容不删除。</summary>
    partial void OnClipboardHistoryEnabledChanged(bool value)
    {
        if (value == _settings.ClipboardHistoryEnabled)
            return;

        _settings = _settings with { ClipboardHistoryEnabled = value };
        _store.Save(_settings);
        ClipboardHistoryEnabledChanged?.Invoke(value);
        StatusMessage = value ? "已恢复记录剪贴板历史" : "已暂停记录剪贴板历史（已记录的内容保留）";
    }

    /// <summary>剪贴板保留策略改完立即清理一次；容量以 MB 存配置。</summary>
    partial void OnClipboardRetentionDaysChanged(int value) => PersistClipboardRetention();

    partial void OnClipboardMaxMbChanged(long value) => PersistClipboardRetention();

    private void PersistClipboardRetention()
    {
        var maxBytes = Math.Max(0, ClipboardMaxMb) * 1024 * 1024;

        if (ClipboardRetentionDays == _settings.ClipboardHistoryRetentionDays
            && maxBytes == _settings.ClipboardHistoryMaxBytes)
        {
            return;
        }

        _settings = _settings with
        {
            ClipboardHistoryRetentionDays = ClipboardRetentionDays,
            ClipboardHistoryMaxBytes = maxBytes
        };

        _store.Save(_settings);
        ClipboardRetentionChanged?.Invoke(ClipboardRetentionDays, maxBytes);
        StatusMessage = "剪贴板保留策略已更新，立即生效";
    }

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
    /// 检查更新：Velopack 只在安装版里可用；开发态（dotnet run）会明确告知。
    /// 地址留空或不是 http(s) 时直接返回提示，不去碰网络。
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        var url = UpdateFeedUrl.Trim();

        if (url.Length == 0)
        {
            UpdateStatus = "未配置更新源";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var feed)
            || (feed.Scheme != Uri.UriSchemeHttp && feed.Scheme != Uri.UriSchemeHttps))
        {
            UpdateStatus = "更新源必须是 http(s) 地址";
            return;
        }

        // 记住地址，下次进来直接可用
        if (!string.Equals(url, _settings.UpdateFeedUrl, StringComparison.Ordinal))
        {
            _settings = _settings with { UpdateFeedUrl = url };
            _store.Save(_settings);
        }

        IsCheckingUpdate = true;
        UpdateStatus = "正在检查…";

        try
        {
            var manager = new Velopack.UpdateManager(url);

            if (!manager.IsInstalled)
            {
                UpdateStatus = "当前不是安装版，无法检查更新";
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                UpdateStatus = "已是最新版本";
                return;
            }

            UpdateStatus = $"发现 {update.TargetFullRelease.Version}，正在下载…";
            await manager.DownloadUpdatesAsync(update);

            UpdateStatus = "下载完成，正在重启…";
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            UpdateStatus = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>
    /// 保存自托管镜像地址（空 = 只用内置源）。
    /// 下载器运行期可改，不需要重建；下一次点「下载」就按新地址走。
    /// </summary>
    public void SaveLocalModelMirror()
    {
        var mirror = LocalModelMirror.Trim();

        if (string.Equals(mirror, _settings.LocalModelMirror, StringComparison.Ordinal))
        {
            StatusMessage = "镜像地址未变化";
            return;
        }

        _settings = _settings with { LocalModelMirror = mirror };
        _store.Save(_settings);

        if (_localModels is LocalModelManager manager)
            manager.MirrorUrl = mirror;

        StatusMessage = mirror.Length == 0 ? "已恢复内置下载源" : "镜像地址已保存";
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

/// <summary>窗口元素 API 选项：Key 存进设置，Label 给界面看。</summary>
public sealed record SettingsChoiceOption(string Key, string Label);
