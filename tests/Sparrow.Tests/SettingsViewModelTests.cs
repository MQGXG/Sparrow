using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
}
