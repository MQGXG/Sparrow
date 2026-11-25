using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Sparrow.App.History;
using Sparrow.App.Overlays;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.Shell;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App;

public partial class App : Application
{
    /// <summary>
    /// 全局日志入口。M1 阶段还不够复杂，不做 DI 容器；
    /// 换 Serilog 时只改这一处。
    /// </summary>
    public static IAppLog Log { get; private set; } = new FileAppLog();

    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private ToolCatalog? _catalog;
    private PaletteWindow? _palette;
    private PaletteViewModel? _paletteViewModel;
    private WorkspaceWindow? _workspace;
    private WorkspaceViewModel? _workspaceViewModel;
    private CancellationTokenSource? _workspaceRelease;
    private ClipboardPanelWindow? _clipboardPanel;
    private SettingsStore? _settingsStore;
    private AppSettings _appSettings = new();
    private LocalModelManager? _localModels;
    private LocalTranslationService? _localTranslation;
    private AiClient? _ai;

    /// <summary>本次运行是否已同意「发送到模型服务」。</summary>
    private bool _aiConsentGranted;

    /// <summary>正在等待用户确认的那一次（并发调用共用，不叠加弹窗）。</summary>
    private Task<bool>? _aiConsentPending;
    private ToolParameterStore? _parameterStore;
    private IHistoryStore? _history;
    private WindowOverlay? _windowOverlay;
    private ToastService? _toasts;
    private int _toolHotkeyRunning;
    private ClipboardPasteService? _pasteService;
    private SettingsViewModel? _settingsViewModel;

    // 贴图窗口需要宿主一直持有引用，否则会被 GC 回收
    private readonly List<PinnedImageWindow> _pins = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // 关掉窗口不退出：托盘常驻是这类工具的默认行为
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var log = new FileAppLog();
        Log = log;

        // 软件内所有提示信息的统一出口（不在窗口内的地方也能看到）
        _toasts = new ToastService(log);

        // 拖出用的临时文件只保证一次会话；上次留下的按天清理
        ClipboardDragService.CleanupOldFiles();

        // plugins/ 与 Sparrow.App.exe 同级；目录不存在时按「零插件」启动，不报错
        var pluginsRoot = Path.Combine(AppContext.BaseDirectory, "plugins");

        // 设置文件损坏时回退默认值，不影响启动
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "settings.json");
        var settingsStore = new SettingsStore(settingsPath, log);
        var isFirstRun = !File.Exists(settingsPath);
        var settings = settingsStore.Load();

        // 面板的置顶状态要写回同一份 settings
        _settingsStore = settingsStore;
        _appSettings = settings;

        // 工具热键：settings.json 的 toolHotkeys（工具 ID → 热键），格式错误只会跳过该条
        var toolHotkeyPlans = ToolHotkeyPlanner.Build(settings.ToolHotkeys, log);

        // 分类显示名可自定义（categories.json 的 labels 段）；不改插件的分类键
        var categoriesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "categories.json");
        ToolCategories.Configure(new CategoryLabelStore(categoriesPath, log).Load());

        // AI 是宿主基础设施：密钥只在这里解密，插件只拿 IAiClient
        var aiKeyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "ai-key.dat");
        var aiSecrets = new AiSecretStore(aiKeyPath, log);
        var ai = new AiClient(aiSecrets, log);
        ai.Configure(settings);
        ai.ConfirmBeforeSend = ConfirmAiSendAsync;
        _ai = ai;

        // 剪贴板历史：监听 + 落盘都是宿主能力，插件经 IToolHost.ClipboardHistory 只读
        var clipboardHistoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "clipboard.db");
        var clipboardHistory = new ClipboardHistoryStore(
            clipboardHistoryPath,
            log,
            settings.ClipboardHistoryMaxEntries,
            settings.ClipboardHistoryRetentionDays,
            settings.ClipboardHistoryMaxBytes);
        var clipboardMonitor = new ClipboardMonitor(clipboardHistory, log)
        {
            Paused = !settings.ClipboardHistoryEnabled
        };
        clipboardMonitor.Start();

        // 通用窗口覆盖能力：拾取窗口 + 图钉指示（任何插件都可用）
        _windowOverlay = new WindowOverlay(log);

        _localModels = new LocalModelManager(log) { MirrorUrl = settings.LocalModelMirror };
        _localTranslation = new LocalTranslationService(_localModels, log);
        _catalog = new ToolCatalog(pluginsRoot, log, settings, ai, clipboardHistory, _windowOverlay, _localModels, _localTranslation);

        var historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "history.jsonl");
        var history = new JsonlHistoryStore(
            historyPath,
            log,
            settings.HistoryMaxEntries,
            settings.HistoryRetentionDays);
        _history = history;

        // 工具参数的「上次选择」：和插件设置分开，属于宿主 UI 状态
        var parameterPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "parameters.json");
        _parameterStore = new ToolParameterStore(parameterPath, log);

        var clipboard = new Win32Clipboard(log);

        // 顺序粘贴：写剪贴板前抑制历史，热键触发时再模拟 Ctrl+V
        _pasteService = new ClipboardPasteService(clipboard, clipboardMonitor, log);

        // 传方法组而不是实例：第一次搜索时才真正扫描 + 加载插件
        _paletteViewModel = new PaletteViewModel(_catalog.GetRegistry, clipboard, log, history, _parameterStore);
        _palette = new PaletteWindow { DataContext = _paletteViewModel };

        // 工作区与面板共用同一个 ToolCatalog：插件只加载一次
        var startup = new StartupService(log);
        var settingsViewModel = new SettingsViewModel(_catalog, settingsStore, startup, settings, ai, aiSecrets, _localModels);
        _settingsViewModel = settingsViewModel;

        // 设置页的状态提示统一转成 toast（页面本身不再常驻一行文字）
        settingsViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.StatusMessage)
                && settingsViewModel.StatusMessage is { Length: > 0 } message)
            {
                _toasts?.Show(message);
            }
        };

        // 设置页改完保留策略后，历史存储立即按新规则清理一次
        settingsViewModel.HistoryRetentionChanged += history.Configure;
        settingsViewModel.ClipboardHistoryEnabledChanged += enabled => clipboardMonitor.Paused = !enabled;

        // 「不记录」名单：启动时给监听器一份，设置页改完立即生效
        clipboardMonitor.SetExcludedApps(settings.ClipboardExcludedApps);
        settingsViewModel.ClipboardExclusionsChanged += clipboardMonitor.SetExcludedApps;

        // 剪贴板保留策略改完立即清理一次
        settingsViewModel.ClipboardRetentionChanged += clipboardHistory.ConfigureRetention;
        _workspaceViewModel = new WorkspaceViewModel(
            _catalog,
            clipboard,
            log,
            history,
            settingsViewModel,
            _parameterStore,
            clipboardHistory: clipboardHistory,
            pasteService: _pasteService,
            loadClipboardOnCreate: false);

        // 工作台窗口在第一次打开时才创建：启动阶段只保留 ViewModel（很小）

        var (searchModifiers, searchKey) = ResolveHotkey(settings.SearchHotkey, "Ctrl+Alt+S");
        var (captureModifiers, captureKey) = ResolveHotkey(settings.CaptureHotkey, "Ctrl+Alt+C");
        var (queueModifiers, queueKey) = ResolveHotkey(settings.QueuePasteHotkey, "Ctrl+Alt+V");
        var (panelModifiers, panelKey) = ResolveHotkey(settings.ClipboardPanelHotkey, "Ctrl+Alt+B");

        if (_pasteService is not null)
            _pasteService.TriggerModifiers = queueModifiers;

        // 托盘菜单的名称只读插件清单（toolId → 插件显示名），不加载插件程序集
        IReadOnlyDictionary<string, string> ReadToolNames()
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var candidate in _catalog?.ScanCandidates() ?? [])
            {
                var manifest = candidate.Manifest;
                if (manifest is null)
                    continue;

                foreach (var toolId in manifest.Tools)
                    names[toolId] = manifest.Name;
            }

            return names;
        }

        var toolNames = ReadToolNames();

        var trayEntries = TrayMenuPlanner.Build(
            settings.TrayMenu,
            settings.ToolHotkeys,
            toolNames,
            HotkeyText.Format(searchModifiers, searchKey),
            HotkeyText.Format(captureModifiers, captureKey),
            log);

        _tray = new TrayService(
            _palette,
            ShowWorkspace,
            CaptureAndRecognize,
            trayEntries,
            toolId => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _ = ExecuteToolHotkeyAsync(toolId)));
        _tray.Initialize();

        var hotkeyBindings = new List<HotkeyBinding>
        {
            new(
                0x5A5A,
                searchModifiers | HotkeyService.ModNoRepeat,
                searchKey,
                () =>
                {
                    var pressedAt = Stopwatch.GetTimestamp();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _palette?.Toggle();
                        Log.Info($"热键唤起：{Stopwatch.GetElapsedTime(pressedAt).TotalMilliseconds:F0} ms");
                    });
                },
                "搜索面板"),

            new(
                0x5A5B,
                captureModifiers | HotkeyService.ModNoRepeat,
                captureKey,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(CaptureAndRecognize),
                "截图识别"),

            new(
                0x5A5C,
                queueModifiers | HotkeyService.ModNoRepeat,
                queueKey,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(PasteNextFromQueue),
                "顺序粘贴"),

            new(
                0x5A5D,
                panelModifiers | HotkeyService.ModNoRepeat,
                panelKey,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(ToggleClipboardPanel),
                "剪贴板面板")
        };

        // 工具热键：按下直接执行，不打开面板
        foreach (var plan in toolHotkeyPlans)
        {
            var toolId = plan.ToolId;
            var label = toolNames.TryGetValue(toolId, out var toolName) ? toolName : toolId;

            hotkeyBindings.Add(new HotkeyBinding(
                plan.Id,
                plan.Modifiers | HotkeyService.ModNoRepeat,
                plan.VirtualKey,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => _ = ExecuteToolHotkeyAsync(toolId)),
                label));
        }

        _hotkey = new HotkeyService(hotkeyBindings);

        if (!_hotkey.Register())
        {
            var failed = _hotkey.FailedBindings.Count > 0
                ? string.Join(
                    "，",
                    _hotkey.FailedBindings.Select(binding =>
                        $"{binding.Label} {HotkeyText.Format(binding.Modifiers, binding.VirtualKey)}".Trim()))
                : "未知热键";

            var message = $"热键注册失败（可能被其他软件占用）：{failed}";
            _tray.SetStatus("Sparrow · " + message);
            _toasts?.Show(message, TimeSpan.FromSeconds(6));
            log.Error(message);
        }

        desktop.Exit += (_, _) =>
        {
            CancelWorkspaceRelease();
            _toasts?.Dispose();
            _windowOverlay?.Dispose();
            _hotkey?.Dispose();
            _catalog?.Dispose();
            clipboardMonitor.Dispose();
        };

        // --workspace：启动后直接打开工作区（快捷方式/开机自启可用）
        if (desktop.Args?.Contains("--workspace") == true)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowWorkspace);
        }
        else if (isFirstRun)
        {
            // 首次运行：应用只启动到托盘，用户会以为「没启动」。
            // 主动展示一次主界面，之后恢复纯托盘行为。
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _palette?.Toggle(),
                Avalonia.Threading.DispatcherPriority.Background);
            log.Info("首次运行：显示一次命令面板");
        }

        Log.Info($"启动完成：{Program.Startup.ElapsedMilliseconds} ms，托盘就绪");

        base.OnFrameworkInitializationCompleted();
    }

}
