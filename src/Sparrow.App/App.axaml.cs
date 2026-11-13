using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Sparrow.App.History;
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
    private ToolParameterStore? _parameterStore;
    private IHistoryStore? _history;

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

        // AI 是宿主基础设施：密钥只在这里解密，插件只拿 IAiClient
        var aiKeyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "ai-key.dat");
        var aiSecrets = new AiSecretStore(aiKeyPath, log);
        var ai = new AiClient(aiSecrets, log);
        ai.Configure(settings);

        // 剪贴板历史：监听 + 落盘都是宿主能力，插件经 IToolHost.ClipboardHistory 只读
        var clipboardHistoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "clipboard-history.jsonl");
        var clipboardHistory = new ClipboardHistoryStore(
            clipboardHistoryPath,
            log,
            settings.ClipboardHistoryMaxEntries);
        var clipboardMonitor = new ClipboardMonitor(clipboardHistory, log)
        {
            Paused = !settings.ClipboardHistoryEnabled
        };
        clipboardMonitor.Start();

        _catalog = new ToolCatalog(pluginsRoot, log, settings, ai, clipboardHistory);

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

        // 传方法组而不是实例：第一次搜索时才真正扫描 + 加载插件
        _paletteViewModel = new PaletteViewModel(_catalog.GetRegistry, clipboard, log, history, _parameterStore);
        _palette = new PaletteWindow { DataContext = _paletteViewModel };

        // 工作区与面板共用同一个 ToolCatalog：插件只加载一次
        var startup = new StartupService(log);
        var settingsViewModel = new SettingsViewModel(_catalog, settingsStore, startup, settings, ai, aiSecrets);

        // 设置页改完保留策略后，历史存储立即按新规则清理一次
        settingsViewModel.HistoryRetentionChanged += history.Configure;
        settingsViewModel.ClipboardHistoryEnabledChanged += enabled => clipboardMonitor.Paused = !enabled;
        _workspaceViewModel = new WorkspaceViewModel(_catalog, clipboard, log, history, settingsViewModel, _parameterStore);
        _workspace = new WorkspaceWindow { DataContext = _workspaceViewModel };

        _tray = new TrayService(_palette, ShowWorkspace, CaptureAndRecognize);
        _tray.Initialize();

        var (searchModifiers, searchKey) = ResolveHotkey(settings.SearchHotkey, "Ctrl+Alt+S");
        var (captureModifiers, captureKey) = ResolveHotkey(settings.CaptureHotkey, "Ctrl+Alt+C");

        _hotkey = new HotkeyService(
        [
            new HotkeyBinding(
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
                }),

            new HotkeyBinding(
                0x5A5B,
                captureModifiers | HotkeyService.ModNoRepeat,
                captureKey,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(CaptureAndRecognize))
        ]);

        if (!_hotkey.Register())
        {
            _tray.SetStatus("Sparrow · 热键注册失败（Ctrl+Alt+S / Ctrl+Alt+C 可能被占用）");
            log.Error("全局热键注册失败，可能被其他程序占用。托盘菜单仍可用。");
        }

        desktop.Exit += (_, _) =>
        {
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
            CopyText = CopyTextToClipboard
        };
        capture.Show();
        capture.Activate();
        Log.Info($"截图窗口已显示：visible={capture.IsVisible}，title={capture.Title}");

        await WaitForClosedAsync(capture);

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
        CancellationToken cancellationToken)
    {
        if (_catalog is null)
            return CaptureOcrResult.Failed("工具目录尚未就绪");

        var entry = _catalog.GetRegistry()
            .FindAccepting(InputShape.Image)
            .FirstOrDefault();

        if (entry is null)
            return CaptureOcrResult.Failed("没有能识别图片的工具");

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var context = new ToolContext
            {
                Input = new ImageInput(png, ImageFormat.Png),
                Source = ToolSource.Screenshot,
                Parameters = BuildParameters(entry),
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
