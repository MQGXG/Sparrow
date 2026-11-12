using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.Shell;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;

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
        var settings = new SettingsStore(settingsPath, log).Load();

        _catalog = new ToolCatalog(pluginsRoot, log, settings);

        var clipboard = new Win32Clipboard(log);

        // 传方法组而不是实例：第一次搜索时才真正扫描 + 加载插件，
        // 保证冷启动路径上不加载任何插件程序集（docs/plugin-model.md 第六节）
        var viewModel = new PaletteViewModel(_catalog.GetRegistry, clipboard, log);

        var palette = new PaletteWindow { DataContext = viewModel };
        _tray = new TrayService(palette);
        _tray.Initialize();

        // 热键在独立线程的消息循环里触发，必须切回 UI 线程再操作窗口
        _hotkey = new HotkeyService(
            () => Avalonia.Threading.Dispatcher.UIThread.Post(palette.Toggle));

        // 注册失败必须让用户看见：热键被别的软件占用是最常见的「装了没反应」
        if (!_hotkey.Register())
        {
            _tray.SetStatus("Sparrow · Ctrl+Alt+S 注册失败（可能被占用）");
            log.Error("全局热键 Ctrl+Alt+S 注册失败，可能被其他程序占用。托盘菜单仍可用。");
        }

        desktop.Exit += (_, _) =>
        {
            _hotkey?.Dispose();
            _catalog?.Dispose();
        };

        base.OnFrameworkInitializationCompleted();
    }
}
