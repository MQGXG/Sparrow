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
using Sparrow.App.Services.Recording;
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

        // UI 线程异常：落盘后标记 Handled，避免一个未捕获异常直接带走整个壳
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.Error("UI 线程未处理异常", e.Exception);
            e.Handled = true;
        };

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

        // 旧版本剪贴板只留 200 条：容量才用几个百分点就开始丢历史。
        // 升级时抬到 1000，用户手动改成别的值不动。
        if (settings.ClipboardHistoryMaxEntries == 200)
        {
            settings = settings with { ClipboardHistoryMaxEntries = 1000 };
            settingsStore.Save(settings);
        }

        // 可变数据（剪贴板/模型/录制/历史）跟随用户设置的数据目录。
        SparrowPaths.Apply(
            settings.DataDirectory,
            settings.ModelDirectory,
            settings.RecordDirectory);

        // 面板的置顶状态要写回同一份 settings
        _settingsStore = settingsStore;
        _appSettings = settings;

        // 外观要在创建任何窗口之前应用，避免启动时先深后浅闪一下。
        // 默认 dark，与历史版本外观一致。
        ThemeService.Apply(settings.Appearance);

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
        var clipboardHistoryPath = SparrowPaths.ClipboardDb;
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

        var historyPath = SparrowPaths.HistoryFile;
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

        // 设置页 / 顶栏切换外观：即时换肤，并同步宿主持有的设置快照
        settingsViewModel.AppearanceChanged += appearance =>
        {
            _appSettings = _appSettings with { Appearance = appearance };
            ThemeService.Apply(appearance);
        };

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
            () => StartScreenRecording(),
            trayEntries,
            toolId => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _ = ExecuteToolHotkeyAsync(toolId)));
        _tray.Initialize();

        // 录制热键可配置；解析失败时退回默认 Ctrl+Alt+Shift+R。
        var recordHotkeyModifiers =
            HotkeyService.ModControl | HotkeyService.ModAlt | HotkeyService.ModShift;
        var recordHotkeyKey = 0x52u;

        if (HotkeyText.TryParse(
                settings.RecordHotkey,
                out var parsedRecordModifiers,
                out var parsedRecordKey))
        {
            recordHotkeyModifiers = parsedRecordModifiers;
            recordHotkeyKey = parsedRecordKey;
        }

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
                "剪贴板面板"),

            new(
                0x5A5E,
                recordHotkeyModifiers | HotkeyService.ModNoRepeat,
                recordHotkeyKey,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(ToggleScreenRecording),
                "屏幕录制")
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
        else if (desktop.Args?.Contains("--record") == true)
        {
            // 开发/自动化入口：跳过设置面板，直接全屏录制并显示控制条。
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var setup = Environment.GetEnvironmentVariable("SPARROW_RECORD_CAMERA") == "1"
                    ? DefaultRecordingSetup with { Camera = true }
                    : DefaultRecordingSetup;

                _ = StartRecordingCoreAsync(WgcCapture.GetPrimaryMonitorBounds(), setup);
            });
        }
        else if (desktop.Args?.Contains("--capture") == true)
        {
            // 开发/自动化入口：直接打开截图窗口，用于验证选区工具条。
            Avalonia.Threading.Dispatcher.UIThread.Post(CaptureAndRecognize);
        }
        else if (desktop.Args?.Contains("--setup") == true)
        {
            // 开发/自动化入口：直接打开录制设置面板。
            Avalonia.Threading.Dispatcher.UIThread.Post(OpenRecordingSetup);
        }
        else if (desktop.Args?.Contains("--watermark") == true)
        {
            // 开发/自动化入口：直接打开水印编辑器。
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => new WatermarkEditorWindow(
                    Sparrow.App.Services.Recording.WatermarkLayout.Default).Show());
        }
        else if (desktop.Args?.Contains("--countdown") == true)
        {
            // 开发/自动化入口：直接显示录制倒计时。
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => new CountdownWindow(3).Show());
        }
        else if (desktop.Args?.Contains("--record-region") == true)
        {
            // 开发/自动化入口：录一个固定区域，验证控制条贴在选区附近。
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _ = StartRecordingCoreAsync(
                    new PixelRect(200, 200, 1000, 700),
                    DefaultRecordingSetup));
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

        // 开发期录制自检（SPARROW_RECORD_PROBE=5）：录固定时长后退出。
        // 正常启动时环境变量不存在，这里是空操作，不产生任何副作用。
        var probe = RecordingProbe.TryStart(out var probeSeconds, out var probeError);

        if (probeError is not null)
            log.Error($"[record-probe] {probeError}");
        else if (probe is not null)
            _ = RunRecordingProbeAsync(probe, probeSeconds, log);

        Log.Info($"启动完成：{Program.Startup.ElapsedMilliseconds} ms，托盘就绪");

        // 首次打开命令面板会触发插件扫描 + 程序集加载。
        // 启动完成后先在后台预热一次，避免第一次 Ctrl+Alt+S 在 UI 线程上等待。
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        _catalog?.GetRegistry();
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"插件预热失败：{ex.Message}");
                    }
                });
            },
            Avalonia.Threading.DispatcherPriority.Background);

        base.OnFrameworkInitializationCompleted();
    }

}
