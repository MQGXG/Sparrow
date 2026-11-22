using System.Diagnostics;
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
    private ClipboardPanelWindow? _clipboardPanel;
    private SettingsStore? _settingsStore;
    private AppSettings _appSettings = new();
    private LocalModelManager? _localModels;
    private LocalTranslationService? _localTranslation;

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
            pasteService: _pasteService);
        _workspace = new WorkspaceWindow { DataContext = _workspaceViewModel };

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

    /// <summary>
    /// 热键呼出 / 收起剪贴板快取面板。面板首次使用时才创建，之后复用同一实例。
    /// 数据直接取主窗的剪贴板视图模型，复制后主窗刷新时保持一致。
    /// </summary>
    private void ToggleClipboardPanel()
    {
        if (_workspaceViewModel?.Clipboard is not { } clipboard)
            return;

        if (_clipboardPanel is { IsVisible: true })
        {
            _clipboardPanel.HidePanel();
            return;
        }

        _clipboardPanel ??= new ClipboardPanelWindow
        {
            DataContext = clipboard,
            SettingsStore = _settingsStore,
            Settings = _appSettings,
        };

        clipboard.Refresh();
        _clipboardPanel.Show();
        _clipboardPanel.Activate();
        _clipboardPanel.PrepareForShow();
    }

    /// <summary>
    /// 发送前确认：本次运行第一次调用 AI 时弹一次，展示服务、模型和将要发送的内容。
    /// 取消后请求不会发出；下次调用会重新询问。并发调用共用同一个弹窗。
    /// </summary>
    private Task<bool> ConfirmAiSendAsync(AiRequest request)
    {
        if (_aiConsentGranted)
            return Task.FromResult(true);

        return _aiConsentPending ??= AskAiConsentAsync(request);
    }

    /// <summary>
    /// 截图工具条「翻译」的语言列表：直接读 OCR 工具的「翻译为」声明，跳过「不翻译」。
    /// 读不到（插件还没加载/加载失败）就返回空，按钮自己隐藏。
    /// </summary>
    private IReadOnlyList<(string Value, string Label)> TranslateTargets()
    {
        if (_catalog is null)
            return [];

        try
        {
            var entry = _catalog.GetRegistry()
                .FindAccepting(InputShape.Images)
                .FirstOrDefault();

            var parameter = entry?.Descriptor.Parameters
                .FirstOrDefault(item => item.Key == "translate");

            if (parameter is null)
                return [];

            return parameter.Choices
                .Where(choice => choice != "none")
                .Select(choice => (
                    choice,
                    parameter.ChoiceLabels is { } labels && labels.TryGetValue(choice, out var label)
                        ? label
                        : choice))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取截图翻译语言失败：{ex.Message}");
            return [];
        }
    }

    private Task<bool> AskAiConsentAsync(AiRequest request)
    {
        var completion = new TaskCompletionSource<bool>();

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var window = new AiConsentWindow(
                    DescribeAiTarget(),
                    _appSettings.AiModel,
                    FormatAiPreview(request));

                window.Show();
                var approved = await window.Result;

                if (approved)
                {
                    if (window.Remember)
                        _aiConsentGranted = true;

                    completion.TrySetResult(true);
                }
                else
                {
                    // 用户拒绝：清掉缓存，下次调用重新问
                    _aiConsentPending = null;
                    completion.TrySetResult(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"发送确认弹窗失败，已取消本次请求：{ex.Message}");
                _aiConsentPending = null;
                completion.TrySetResult(false);
            }
        });

        return completion.Task;
    }

    /// <summary>弹窗第一行：提供方名 + 主机名（不显示完整路径和密钥）。</summary>
    private string DescribeAiTarget()
    {
        var provider = AiProviders.Find(_appSettings.AiProvider)?.Name ?? "自定义";
        var host = Uri.TryCreate(_appSettings.AiBaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : _appSettings.AiBaseUrl;

        return $"{provider} · {host}";
    }

    /// <summary>预览要发送的内容：系统提示 + 每条消息，长文本截断。</summary>
    private static string FormatAiPreview(AiRequest request)
    {
        var builder = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.System))
            builder.AppendLine("【系统提示】").AppendLine(request.System.Trim()).AppendLine();

        foreach (var message in request.Messages)
        {
            builder.Append('【').Append(message.Role switch
            {
                AiRole.System => "系统",
                AiRole.Assistant => "模型",
                _ => "用户"
            }).Append("】 ").AppendLine(message.Text);
        }

        if (request.Image.Length > 0)
            builder.AppendLine().Append("【附带图片】").Append(request.ImageMimeType ?? "image");

        var text = builder.ToString().Trim();
        return text.Length <= 800 ? text : text[..800] + $"\n…（共 {text.Length} 字）";
    }

    /// <summary>从托盘打开工作区。窗口先显示，插件在后台加载。</summary>
    private void ShowWorkspace()
    {
        if (_workspace is null)
            return;

        _workspace.Show();
        _workspace.Activate();
        _ = _workspaceViewModel?.ReloadAsync();
    }

    /// <summary>
    /// 截图入口：选区 → 动作（识别 / 复制 / 保存 / 固定 / 滚动）。
    /// 截图是宿主能力，OCR 仍然是普通插件——宿主只是替它准备 ImageInput。
    /// </summary>
    private async void CaptureAndRecognize()
    {
        if (_palette is null || _paletteViewModel is null || _catalog is null)
            return;

        var capture = new CaptureWindow
        {
            OcrRunner = RunCaptureOcrAsync,
            CopyText = CopyTextToClipboard,
            FreezeOnOpen = true,
            CaptureOcrBoxMode = _settingsViewModel?.CaptureOcrBoxMode ?? OcrBoxMode.Hover,
            NoteColorValue = ParseNoteColor(_appSettings.AnnotationColor),
            NoteStrokeWidth = _appSettings.AnnotationWidth,
            NoteMosaicBlur = _appSettings.AnnotationMosaicBlur,
            SizeInPixels = _appSettings.CaptureSizeUnit != "dip",
            ExportCornerRadius = _appSettings.CaptureCornerRadius,
            CaptureToolPositions = _appSettings.CaptureToolPositions,
            CaptureToolHidden = _appSettings.CaptureToolHidden
        };

        // 三槽样式模板：灌进去，关闭时读回
        for (var index = 0; index < capture.Presets.Length
             && index < _appSettings.AnnotationPresets.Count; index++)
        {
            capture.Presets[index] = _appSettings.AnnotationPresets[index];
        }

        capture.OcrBoxModeChanged += mode =>
            _settingsViewModel?.SetCaptureOcrBoxMode(OcrBoxModes.Format(mode));
        capture.Show();
        capture.Activate();

        // 窗口先出来再读语言列表：首次会顺手加载插件，别让它卡在显示之前
        capture.ConfigureTranslateTargets(TranslateTargets());
        Log.Info($"截图窗口已显示：visible={capture.IsVisible}，title={capture.Title}");

        await WaitForClosedAsync(capture);

        // 记住这次的标注样式和尺寸单位，下次截图直接沿用
        SaveCapturePreferences(capture);

        // 旧 OCR 插件只返回文本：窗口已关，直接回退到面板，避免重复识别
        if (capture.FallbackText is { Length: > 0 } fallback)
        {
            _paletteViewModel.ShowExternalResult("截图识别", fallback);
            _paletteViewModel.CopyResult();
            return;
        }

        if (capture.Result is not { Length: > 0 } png)
            return;

        if (capture.Action == CaptureAction.Scroll)
        {
            await RunScrollCaptureAsync(capture.SelectionRect);
            return;
        }

        await HandleCaptureActionAsync(png, capture.Action);
    }

    /// <summary>截图窗口关掉后，把标注样式和尺寸单位写回设置（变了才写）。</summary>
    private void SaveCapturePreferences(CaptureWindow capture)
    {
        var color = $"#{capture.NoteColorValue & 0xFFFFFF:X6}";
        var unit = capture.SizeInPixels ? "px" : "dip";
        var presets = capture.Presets.Where(preset => preset is not null).Cast<AnnotationPreset>().ToList();
        var toolPositions = capture.CaptureToolPositions.ToList();
        var toolHidden = capture.CaptureToolHidden.ToList();

        if (color == _appSettings.AnnotationColor
            && Math.Abs(capture.NoteStrokeWidth - _appSettings.AnnotationWidth) < 0.01
            && capture.NoteMosaicBlur == _appSettings.AnnotationMosaicBlur
            && unit == _appSettings.CaptureSizeUnit
            && Math.Abs(capture.ExportCornerRadius - _appSettings.CaptureCornerRadius) < 0.01
            && presets.SequenceEqual(_appSettings.AnnotationPresets)
            && toolPositions.SequenceEqual(_appSettings.CaptureToolPositions)
            && toolHidden.SequenceEqual(_appSettings.CaptureToolHidden))
        {
            return;
        }

        _appSettings = _appSettings with
        {
            AnnotationColor = color,
            AnnotationWidth = capture.NoteStrokeWidth,
            AnnotationMosaicBlur = capture.NoteMosaicBlur,
            CaptureSizeUnit = unit,
            CaptureCornerRadius = capture.ExportCornerRadius,
            AnnotationPresets = presets,
            CaptureToolPositions = toolPositions,
            CaptureToolHidden = toolHidden
        };

        try
        {
            _settingsStore?.Save(_appSettings);
        }
        catch (Exception ex)
        {
            Log.Warn($"保存截图样式失败：{ex.Message}");
        }
    }

    private static uint ParseNoteColor(string? value) =>
        uint.TryParse(
            value?.TrimStart('#'),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var rgb)
            ? 0xFF000000u | (rgb & 0xFFFFFF)
            : 0xFFE5A44Cu;

    /// <summary>截图结果的动作分发：普通截图和滚动截图共用。</summary>
    private async Task HandleCaptureActionAsync(byte[] png, CaptureAction action)
    {
        switch (action)
        {
            case CaptureAction.CopyImage:
                CopyImageToClipboard(png);
                return;

            case CaptureAction.Save:
                await SaveImageAsync(png);
                return;

            case CaptureAction.Pin:
                PinImage(png);
                return;

            case CaptureAction.Recognize:
                await RecognizeImageAsync(png);
                return;
        }
    }

    private void CopyImageToClipboard(byte[] png)
    {
        try
        {
            var clipboardForImage = new Win32Clipboard(Log);
            if (clipboardForImage.SetImagePng(png))
                Log.Info("截图已复制到剪贴板");
            else
                Log.Warn("复制图片到剪贴板失败");
        }
        catch (Exception ex)
        {
            Log.Error("复制图片到剪贴板失败", ex);
        }
    }

    /// <summary>
    /// 截图窗口里的就地 OCR：宿主准备输入，插件仍然只收一张 ImageInput。
    /// 识别成功写历史；旧插件只返回文本时由窗口回退到面板。
    /// </summary>
    private async Task<CaptureOcrResult> RunCaptureOcrAsync(
        byte[] png,
        CaptureOcrOptions options,
        CancellationToken cancellationToken)
    {
        if (_catalog is null)
            return CaptureOcrResult.Failed("工具目录尚未就绪");

        var entry = _catalog.GetRegistry()
            .FindAccepting(InputShape.Images)
            .FirstOrDefault();

        if (entry is null)
            return CaptureOcrResult.Failed("没有能识别图片的工具");

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var parameters = BuildParameters(entry);

            // 截图工具的「翻译」下拉：覆盖 OCR 上次的选择，本次生效
            if (options.TranslateTarget is { Length: > 0 } target)
                parameters["translate"] = target;

            var context = new ToolContext
            {
                Input = new ImageSetInput(
                [
                    new ImageInput(png, ImageFormat.Png) { Name = "截图" }
                ]),
                Source = ToolSource.Screenshot,
                Parameters = parameters,
                Host = entry.Host
            };

            var result = await entry.Tool.ExecuteAsync(context, cancellationToken);

            if (!result.Success)
                return CaptureOcrResult.Failed(result.Error?.Message ?? "识别失败");

            var elapsed = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            switch (result.Payload)
            {
                case OcrResultPayload ocr:
                    AddCaptureHistory(entry, png, ocr.Text, elapsed);
                    return new CaptureOcrResult(ocr.Text, ocr.Lines);

                case TextPayload text:
                    AddCaptureHistory(entry, png, text.Text, elapsed);
                    return new CaptureOcrResult(text.Text, Array.Empty<OcrTextLine>());

                default:
                    return CaptureOcrResult.Failed("识别结果格式不受支持");
            }
        }
        catch (OperationCanceledException)
        {
            return CaptureOcrResult.Failed("已取消");
        }
        catch (Exception ex)
        {
            Log.Error("截图就地识别失败", ex);
            return CaptureOcrResult.Failed($"识别失败：{ex.Message}");
        }
    }

    /// <summary>窗口只负责发命令，剪贴板仍然是宿主能力。</summary>
    private void CopyTextToClipboard(string text)
    {
        try
        {
            var clipboard = new Win32Clipboard(Log);
            if (clipboard.SetText(text))
                Log.Info($"复制文本到剪贴板：{text.Length} 字");
            else
                Log.Warn($"复制文本到剪贴板失败：{text.Length} 字");
        }
        catch (Exception ex)
        {
            Log.Error("复制文本失败", ex);
        }
    }

    /// <summary>工具默认参数 + 上次保存的参数；面板与截图共用。</summary>
    private Dictionary<string, string> BuildParameters(ToolEntry entry)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parameter in entry.Descriptor.Parameters)
        {
            if (parameter.Default is not null)
                parameters[parameter.Key] = parameter.Default;
        }

        if (_parameterStore is not null)
        {
            foreach (var (key, value) in _parameterStore.Get(entry.Descriptor.Id))
                parameters[key] = value;
        }

        return parameters;
    }

    private void AddCaptureHistory(ToolEntry entry, byte[] png, string text, long elapsedMs)
    {
        _history?.Add(new HistoryEntry(
            Guid.NewGuid().ToString("N"),
            entry.Descriptor.Id,
            entry.Descriptor.Name,
            text,
            $"截图 {png.Length} 字节",
            DateTimeOffset.Now,
            elapsedMs));
    }

    private async Task RecognizeImageAsync(byte[] png)
    {
        if (_palette is null || _paletteViewModel is null || _catalog is null)
            return;

        Log.Info($"截图完成：{png.Length} 字节");

        // 按输入形态找工具，而不是写死 id：加一个「截图翻译」插件就能直接出现在这里
        var entry = _catalog.GetRegistry()
            .FindAccepting(InputShape.Image)
            .FirstOrDefault();

        if (entry is null)
        {
            _paletteViewModel.ShowExternalError("没有能识别图片的工具");
            _palette.Show();
            _palette.Activate();
            return;
        }

        Log.Info($"截图交给工具：{entry.Descriptor.Id}");

        _paletteViewModel.ShowPending("正在识别截图…");
        _palette.Show();
        _palette.Activate();

        try
        {
            var context = new ToolContext
            {
                Input = new ImageInput(png, ImageFormat.Png),
                Source = ToolSource.Screenshot,
                Parameters = BuildParameters(entry),
                Host = entry.Host
            };

            var result = await entry.Tool.ExecuteAsync(context, CancellationToken.None);

            var text = result.Payload switch
            {
                TextPayload textPayload => textPayload.Text,
                OcrResultPayload ocrPayload => ocrPayload.Text,
                _ => null
            };

            if (result.Success && text is not null)
            {
                _paletteViewModel.ShowExternalResult("截图识别", text);
                _paletteViewModel.CopyResult();
                Log.Info($"截图识别完成：{text.Length} 字符");
            }
            else
            {
                _paletteViewModel.ShowExternalError(result.Error?.Message ?? "识别失败");
                Log.Warn($"截图识别失败：{result.Error?.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("截图识别失败", ex);
            _paletteViewModel.ShowExternalError($"截图识别失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 滚动截图：循环抓帧 → 拼接 → 预览 → 复用普通截图的结果动作。
    ///
    /// 拼接全部发生在宿主，插件仍然只收到一张 ImageInput，
    /// 所以这条功能不需要改 SDK 契约。
    /// </summary>
    private async Task RunScrollCaptureAsync(PixelRect rect)
    {
        if (rect.Width < 8 || rect.Height < 8)
            return;

        using var cts = new CancellationTokenSource();
        var cancelled = false;

        var overlay = new ScrollCaptureWindow();
        overlay.Finished += (_, _) => cts.Cancel();
        overlay.Cancelled += (_, _) =>
        {
            cancelled = true;
            cts.Cancel();
        };

        PositionScrollOverlay(overlay, rect);
        overlay.Show();

        byte[]? png;
        try
        {
            png = await Task.Run(
                () => ScrollCaptureService.CaptureAsync(
                    rect.X,
                    rect.Y,
                    rect.Width,
                    rect.Height,
                    rows => Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => overlay.SetCaptured(rows, rect.Height)),
                    cts.Token),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            png = null;
        }
        finally
        {
            overlay.Close();
        }

        if (cancelled || png is null)
            return;

        Log.Info($"滚动截图完成：{png.Length} 字节，区域 {rect.Width}×{rect.Height}");

        var result = new ScrollResultWindow(png);
        result.Show();
        result.Activate();
        await WaitForClosedAsync(result);

        if (result.Action == CaptureAction.None)
            return;

        await HandleCaptureActionAsync(png, result.Action);
    }

    /// <summary>把提示条放在选区外（下方放不下就放上方），避免被自己截进去。</summary>
    private static void PositionScrollOverlay(Window overlay, PixelRect selection)
    {
        var center = new PixelPoint(
            selection.X + selection.Width / 2,
            selection.Y + selection.Height / 2);

        var screen = overlay.Screens.ScreenFromPoint(center) ?? overlay.Screens.Primary;
        var scaling = screen?.Scaling ?? 1;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        var width = (int)(320 * scaling);
        var height = (int)(96 * scaling);

        var areaRight = area.X + area.Width;
        var areaBottom = area.Y + area.Height;

        var x = Math.Clamp(
            selection.X + selection.Width - width,
            area.X + 8,
            Math.Max(area.X + 8, areaRight - width - 8));

        var y = selection.Y + selection.Height + 12;
        if (y + height > areaBottom)
            y = selection.Y - height - 12;
        if (y < area.Y + 8)
            y = area.Y + 8;

        overlay.Position = new PixelPoint(x, y);
    }

    /// <summary>弹保存对话框，把截图写成 PNG。</summary>
    private async Task SaveImageAsync(byte[] png)
    {
        var owner = (Window?)_palette ?? _workspace;
        if (owner is null)
            return;

        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存截图",
                SuggestedFileName = $"Sparrow-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                DefaultExtension = "png",
                FileTypeChoices =
                [
                    new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }
                ]
            });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
            Log.Info($"截图已保存：{file.Path}");
        }
        catch (Exception ex)
        {
            Log.Error("保存截图失败", ex);
        }
    }

    /// <summary>把截图钉到屏幕上，可同时存在多张。</summary>
    private void PinImage(byte[] png)
    {
        try
        {
            var pin = new PinnedImageWindow(png);
            pin.Closed += (_, _) => _pins.Remove(pin);
            _pins.Add(pin);
            pin.Show();
            Log.Info("贴图已显示");
        }
        catch (Exception ex)
        {
            Log.Error("显示贴图失败", ex);
        }
    }

    /// <summary>
    /// 工具热键 / 托盘菜单的统一执行入口：不打开面板，直接用同一个上下文构建逻辑执行，
    /// 完成后把结果交给面板展示（长时间交互类工具的结果也写进历史）。
    /// </summary>
    /// <summary>
    /// 全局热键：粘贴队列下一条。
    /// 热键在目标应用里按下时，Sparrow 不抢焦点；写剪贴板后注入 Ctrl+V。
    /// </summary>
    private void PasteNextFromQueue()
    {
        var clipboard = _workspaceViewModel?.Clipboard;

        if (clipboard is null || !clipboard.HasQueue)
        {
            _toasts?.Show("粘贴队列为空：先到「剪贴板」页把条目加入队列", TimeSpan.FromSeconds(4));
            return;
        }

        if (!clipboard.PasteNext(simulate: true))
            _toasts?.Show("顺序粘贴失败：下一条没有可粘贴内容", TimeSpan.FromSeconds(4));
    }

    private async Task ExecuteToolHotkeyAsync(string toolId)
    {
        if (_catalog is null || _palette is null || _paletteViewModel is null)
            return;

        // 上一个热键工具还没结束（比如正在拾取窗口）：忽略，避免叠两次执行
        if (Interlocked.Exchange(ref _toolHotkeyRunning, 1) == 1)
            return;

        try
        {
            var entry = _catalog.GetRegistry()
                .Search(null, int.MaxValue)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Descriptor.Id, toolId, StringComparison.Ordinal));

            if (entry is null)
            {
                Log.Warn($"工具热键对应的工具不存在（可能已禁用或卸载）：{toolId}");
                _toasts?.Show($"工具不存在：{toolId}");
                return;
            }

            var (context, error) = _paletteViewModel.CreateContext(entry);
            if (context is null)
            {
                _toasts?.Show(error ?? "无法构造输入");
                return;
            }

            Log.Info($"工具热键执行：{toolId}");

            var startedAt = Stopwatch.GetTimestamp();
            var result = await entry.Tool.ExecuteAsync(context, CancellationToken.None);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (!result.Success || result.Payload is null)
            {
                Log.Warn($"工具热键执行失败：{toolId}，{result.Error?.Message}");
                _toasts?.Show(result.Error?.Message ?? "执行失败", TimeSpan.FromSeconds(5));
                return;
            }

            var text = PaletteViewModel.FormatPayload(result.Payload);

            // 短结果用 toast 就够；长文本仍然交给面板，方便阅读和复制
            if (text.Length <= 160)
                _toasts?.Show(text);
            else
                _palette.ShowResult(entry.Descriptor.Name, text);

            _history?.Add(new HistoryEntry(
                Guid.NewGuid().ToString("N"),
                entry.Descriptor.Id,
                entry.Descriptor.Name,
                text,
                PaletteViewModel.PreviewInput(context.Input),
                DateTimeOffset.Now,
                (long)elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：轻提示即可，不打扰
            _toasts?.Show("已取消");
        }
        catch (Exception ex)
        {
            Log.Error($"工具热键执行异常：{toolId}", ex);
            _toasts?.Show($"执行异常：{ex.Message}", TimeSpan.FromSeconds(5));
        }
        finally
        {
            Interlocked.Exchange(ref _toolHotkeyRunning, 0);
        }
    }

    private static (uint Modifiers, uint VirtualKey) ResolveHotkey(string? text, string fallback)
    {
        if (HotkeyText.TryParse(text, out var modifiers, out var virtualKey))
            return (modifiers, virtualKey);

        // 配置坏了就退回默认，不让应用因为一个手误的热键起不来
        HotkeyText.TryParse(fallback, out modifiers, out virtualKey);
        return (modifiers, virtualKey);
    }

    private static Task WaitForClosedAsync(Window window)
    {
        var completion = new TaskCompletionSource<bool>();
        window.Closed += (_, _) => completion.TrySetResult(true);
        return completion.Task;
    }
}
