using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
        var settings = settingsStore.Load();

        _catalog = new ToolCatalog(pluginsRoot, log, settings);

        var historyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "history.jsonl");
        var history = new JsonlHistoryStore(historyPath, log);

        var clipboard = new Win32Clipboard(log);

        // 传方法组而不是实例：第一次搜索时才真正扫描 + 加载插件
        _paletteViewModel = new PaletteViewModel(_catalog.GetRegistry, clipboard, log, history);
        _palette = new PaletteWindow { DataContext = _paletteViewModel };

        // 工作区与面板共用同一个 ToolCatalog：插件只加载一次
        var startup = new StartupService(log);
        var settingsViewModel = new SettingsViewModel(_catalog, settingsStore, startup, settings);
        _workspaceViewModel = new WorkspaceViewModel(_catalog, clipboard, log, history, settingsViewModel);
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
        };

        // --workspace：启动后直接打开工作区（快捷方式/开机自启可用）
        if (desktop.Args?.Contains("--workspace") == true)
            Avalonia.Threading.Dispatcher.UIThread.Post(ShowWorkspace);

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
    /// 截图 → OCR → 结果写回面板。
    /// 截图是宿主能力，OCR 仍然是普通插件——宿主只是替它准备 ImageInput。
    /// </summary>
    private async void CaptureAndRecognize()
    {
        if (_palette is null || _paletteViewModel is null || _catalog is null)
            return;

        var capture = new CaptureWindow();
        capture.Show();
        capture.Activate();
        Log.Info($"截图窗口已显示：visible={capture.IsVisible}，title={capture.Title}");

        await WaitForClosedAsync(capture);

        if (capture.Result is not { Length: > 0 } png)
            return;

        if (capture.Action == CaptureAction.CopyImage)
        {
            var clipboardForImage = new Win32Clipboard();
            clipboardForImage.SetImagePng(png);
            return;
        }

        if (capture.Action != CaptureAction.Recognize)
            return;

        Log.Info($"截图完成：{png.Length} 字节");

        var entry = _catalog.GetRegistry()
            .Search("ocr", 20)
            .FirstOrDefault(candidate => candidate.Descriptor.Id == "sparrow.ocr.image");

        if (entry is null)
        {
            _paletteViewModel.ShowExternalError("没有可用的 OCR 工具");
            _palette.Show();
            _palette.Activate();
            return;
        }

        _paletteViewModel.ShowPending("正在识别截图…");
        _palette.Show();
        _palette.Activate();

        try
        {
            var context = new ToolContext
            {
                Input = new ImageInput(png, ImageFormat.Png),
                Host = entry.Host
            };

            var result = await entry.Tool.ExecuteAsync(context, CancellationToken.None);

            if (result.Success && result.Payload is TextPayload text)
            {
                _paletteViewModel.ShowExternalResult("截图识别", text.Text);
                _paletteViewModel.CopyResult();
                Log.Info($"截图识别完成：{text.Text.Length} 字符");
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
