using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>设置页「工具热键」的保存规则：格式化、校验、查重、留空即解绑。</summary>
public sealed class SettingsViewModelTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static (SettingsViewModel ViewModel, SettingsStore Store, ToolCatalog Catalog) Create()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var log = new NullLog();
        var catalog = new ToolCatalog(root, log, new AppSettings());
        var store = new SettingsStore(System.IO.Path.Combine(root, "settings.json"), log);
        var viewModel = new SettingsViewModel(catalog, store, new StartupService(log), new AppSettings());

        return (viewModel, store, catalog);
    }

    [AvaloniaFact]
    public void 设置页_热键框_按键即写入组合键()
    {
        var (viewModel, _, _) = Create();
        viewModel.SelectSection(SettingsSection.Hotkeys);

        var view = new SettingsView { DataContext = viewModel };
        var window = new Avalonia.Controls.Window { Content = view, Width = 900, Height = 700 };
        window.Show();

        var box = view.FindControl<Avalonia.Controls.TextBox>("SearchHotkeyBox");
        box.ShouldNotBeNull();
        box!.Focus();

        window.KeyPress(
            Avalonia.Input.Key.P,
            Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Alt,
            Avalonia.Input.PhysicalKey.P,
            null);

        box.Text.ShouldBe("Ctrl+Alt+P");
        viewModel.SearchHotkey.ShouldBe("Ctrl+Alt+P");
    }

    [Fact]
    public void 设置页_窗口元素API_默认自动且切换后立即保存()
    {
        var (viewModel, store, _) = Create();

        viewModel.SelectedCaptureElementApi.Key.ShouldBe("auto");

        var msaa = viewModel.CaptureElementApiOptions.First(option => option.Key == "msaa");
        viewModel.SelectedCaptureElementApi = msaa;

        store.Load().CaptureElementApi.ShouldBe("msaa");
    }

    [Fact]
    public void 设置页_截图内容选项_默认值与保存()
    {
        var (viewModel, store, _) = Create();

        viewModel.CaptureShowCursor.ShouldBeFalse();
        viewModel.CaptureUiInScroll.ShouldBeTrue();
        viewModel.CaptureRestoreColors.ShouldBeTrue();

        viewModel.CaptureShowCursor = true;
        viewModel.CaptureUiInScroll = false;
        viewModel.CaptureRestoreColors = false;

        var saved = store.Load();
        saved.CaptureShowCursor.ShouldBeTrue();
        saved.CaptureUiInScroll.ShouldBeFalse();
        saved.CaptureRestoreColors.ShouldBeFalse();
    }

    [Fact]
    public void 设置页_冷启动不加载插件_切到热键分段才加载()
    {
        var (viewModel, _, catalog) = Create();

        catalog.IsBuilt.ShouldBeFalse();

        viewModel.SelectSection(SettingsSection.Hotkeys);

        catalog.IsBuilt.ShouldBeTrue();
    }

    [Fact]
    public void 工具热键_保存时格式化并落盘()
    {
        var (viewModel, store, _) = Create();
        viewModel.ToolHotkeys.Add(new ToolHotkeyRowViewModel("sparrow.window.pin", "窗口置顶", "ctrl+alt+p"));

        viewModel.SaveHotkeys();

        store.Load().ToolHotkeys.ShouldContainKeyAndValue("sparrow.window.pin", "Ctrl+Alt+P");
        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("重启");
    }

    [Fact]
    public void 工具热键_留空表示不绑定()
    {
        var (viewModel, store, _) = Create();
        viewModel.ToolHotkeys.Add(new ToolHotkeyRowViewModel("sparrow.window.pin", "窗口置顶", "   "));

        viewModel.SaveHotkeys();

        store.Load().ToolHotkeys.ShouldBeEmpty();
    }

    [Fact]
    public void 工具热键_格式错误_拒绝保存()
    {
        var (viewModel, store, _) = Create();
        viewModel.ToolHotkeys.Add(new ToolHotkeyRowViewModel("sparrow.window.pin", "窗口置顶", "NotAKey"));

        viewModel.SaveHotkeys();

        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("窗口置顶");
        store.Load().ToolHotkeys.ShouldBeEmpty();
    }

    [Fact]
    public void 工具热键_与搜索热键重复_拒绝保存()
    {
        var (viewModel, store, _) = Create();
        viewModel.ToolHotkeys.Add(new ToolHotkeyRowViewModel("sparrow.window.pin", "窗口置顶", "Ctrl+Alt+S"));

        viewModel.SaveHotkeys();

        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("重复");
        store.Load().ToolHotkeys.ShouldBeEmpty();
    }

    [Fact]
    public void 剪贴板面板热键_保存时格式化并落盘()
    {
        var (viewModel, store, _) = Create();
        viewModel.ClipboardPanelHotkey = "ctrl+shift+b";

        viewModel.SaveHotkeys();

        store.Load().ClipboardPanelHotkey.ShouldBe("Ctrl+Shift+B");
        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("重启");
    }

    [Fact]
    public void 剪贴板面板热键_与搜索热键重复_拒绝保存()
    {
        var (viewModel, store, _) = Create();
        viewModel.ClipboardPanelHotkey = viewModel.SearchHotkey;

        viewModel.SaveHotkeys();

        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("重复");
        store.Load().ClipboardPanelHotkey.ShouldBe("Ctrl+Alt+B");
    }

    [Fact]
    public void 剪贴板保留策略_保存并广播()
    {
        var (viewModel, store, _) = Create();

        (int days, long bytes)? received = null;
        viewModel.ClipboardRetentionChanged += (d, b) => received = (d, b);

        viewModel.ClipboardRetentionDays = 30;
        viewModel.ClipboardMaxMb = 64;

        received.ShouldNotBeNull();
        received!.Value.days.ShouldBe(30);
        received.Value.bytes.ShouldBe(64L * 1024 * 1024);

        var saved = store.Load();
        saved.ClipboardHistoryRetentionDays.ShouldBe(30);
        saved.ClipboardHistoryMaxBytes.ShouldBe(64L * 1024 * 1024);
    }

    [Fact]
    public void 排除名单_保存并广播()
    {
        var (viewModel, store, _) = Create();

        IReadOnlyList<string>? received = null;
        viewModel.ClipboardExclusionsChanged += apps => received = apps;

        viewModel.ExcludedApps.ShouldBeEmpty();
        viewModel.SaveExcludedApps(["chrome", "1Password"]);

        received.ShouldNotBeNull();
        received!.ShouldContain("chrome");
        viewModel.ExcludedApps.ShouldContain("1Password");
        store.Load().ClipboardExcludedApps.ShouldBe(["chrome", "1Password"], ignoreOrder: true);

        // 名单没变时不重复落盘
        viewModel.SaveExcludedApps(["chrome", "1Password"]);
        store.Load().ClipboardExcludedApps.Count.ShouldBe(2);
    }

    [Fact]
    public async Task 检查更新_未配置地址时直接提示()
    {
        var (viewModel, _, _) = Create();

        await viewModel.CheckForUpdatesAsync();

        viewModel.UpdateStatus.ShouldBe("未配置更新源");
        viewModel.IsCheckingUpdate.ShouldBeFalse();
    }

    [Fact]
    public async Task 检查更新_非http地址被拒绝且不落盘()
    {
        var (viewModel, store, _) = Create();
        viewModel.UpdateFeedUrl = "ftp://example.com/updates";

        await viewModel.CheckForUpdatesAsync();

        viewModel.UpdateStatus.ShouldBe("更新源必须是 http(s) 地址");
        store.Load().UpdateFeedUrl.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void 设置页_模型分段_显示离线模型列表()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var log = new NullLog();
        var manager = new LocalModelManager(log, root);
        var catalog = new ToolCatalog(root, log, new AppSettings());
        var store = new SettingsStore(System.IO.Path.Combine(root, "settings.json"), log);
        var viewModel = new SettingsViewModel(
            catalog, store, new StartupService(log), new AppSettings(), localModels: manager);

        var view = new SettingsView { DataContext = viewModel };
        var window = new Avalonia.Controls.Window { Content = view, Width = 900, Height = 700 };
        window.Show();

        viewModel.SelectSection(SettingsSection.Model);

        var list = view.FindControl<ItemsControl>("LocalModelsList");
        list.ShouldNotBeNull();
        list!.ItemsSource!.Cast<LocalModelItemViewModel>().Count().ShouldBe(3);

        // 切走分段时整个区块隐藏，别在插件页留半截
        var panel = view.FindControl<Border>("LocalModelsPanel");
        panel.ShouldNotBeNull();
        panel!.IsVisible.ShouldBeTrue();

        viewModel.SelectSection(SettingsSection.Plugins);
        panel.IsVisible.ShouldBeFalse();
    }

    [Fact]
    public void 托盘固定_插在分隔线之前并持久化()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var log = new NullLog();
        var catalog = new ToolCatalog(root, log, new AppSettings());
        var store = new SettingsStore(System.IO.Path.Combine(root, "settings.json"), log);
        var settings = new AppSettings
        {
            TrayMenu = Sparrow.App.Shell.TrayMenuPlanner.BuildDefaultConfig([]),
            TrayMenuCustomized = true
        };
        var viewModel = new SettingsViewModel(catalog, store, new StartupService(log), settings);

        viewModel.SetToolPinnedToTray("sparrow.json.format", true);

        viewModel.IsToolPinnedToTray("sparrow.json.format").ShouldBeTrue();

        var saved = store.Load();
        saved.TrayMenu[^1].Action.ShouldBe("exit");
        saved.TrayMenu[^2].Separator.ShouldBeTrue();
        saved.TrayMenu[^3].Tool.ShouldBe("sparrow.json.format");

        viewModel.SetToolPinnedToTray("sparrow.json.format", false);

        viewModel.IsToolPinnedToTray("sparrow.json.format").ShouldBeFalse();
        store.Load().TrayMenu.ShouldNotContain(item => item.Tool == "sparrow.json.format");
    }

    [Fact]
    public void 托盘编辑_移动删除与添加分隔线()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var log = new NullLog();
        var catalog = new ToolCatalog(root, log, new AppSettings());
        var store = new SettingsStore(System.IO.Path.Combine(root, "settings.json"), log);
        var settings = new AppSettings
        {
            TrayMenu = Sparrow.App.Shell.TrayMenuPlanner.BuildDefaultConfig([]),
            TrayMenuCustomized = true
        };
        var viewModel = new SettingsViewModel(catalog, store, new StartupService(log), settings);

        viewModel.SelectSection(SettingsSection.Tray);

        viewModel.TrayMenuRows.Count.ShouldBe(settings.TrayMenu.Count);
        viewModel.TrayMenuOptions.ShouldNotContain(option => option.Action == "palette");

        // 搜索工具下移一位
        viewModel.MoveTrayRow(viewModel.TrayMenuRows[0], 1);

        store.Load().TrayMenu[0].Action.ShouldBe("clipboard");
        store.Load().TrayMenu[1].Action.ShouldBe("palette");

        // 删除第一项
        viewModel.RemoveTrayRow(viewModel.TrayMenuRows[0]);

        store.Load().TrayMenu[0].Action.ShouldBe("palette");

        // 分隔线插在「退出」之前
        var before = store.Load().TrayMenu.Count;
        viewModel.AddTraySeparator();

        var saved = store.Load();
        saved.TrayMenu.Count.ShouldBe(before + 1);
        saved.TrayMenu[^1].Action.ShouldBe("exit");
        saved.TrayMenu[^2].Separator.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void 外观三张卡_各占一列不重叠()
    {
        var (viewModel, _, _) = Create();
        viewModel.SelectSection(SettingsSection.General);

        var view = new SettingsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();

        var cards = view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("appearance-card"))
            .ToList();

        cards.Count.ShouldBe(3);
        cards.Select(card => Math.Round(card.Bounds.X)).Distinct().Count().ShouldBe(3);
        cards.ShouldAllBe(card => card.Bounds.Width > 100);

        window.Close();
    }

    [Fact]
    public void 快捷键暂停_同步到设置页并可请求恢复()
    {
        var (viewModel, _, _) = Create();

        var requested = false;
        viewModel.ResumeHotkeysRequested += () => requested = true;

        viewModel.SyncHotkeyPolicy(disabled: true, disableInGame: false);

        viewModel.HotkeysDisabled.ShouldBeTrue();
        viewModel.HotkeysActive.ShouldBeFalse();

        viewModel.RequestResumeHotkeys();

        requested.ShouldBeTrue();
    }
}
