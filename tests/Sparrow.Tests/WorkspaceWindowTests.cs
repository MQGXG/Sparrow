using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;

namespace Sparrow.Tests;

/// <summary>
/// 工作区窗口的导航回归：切换行必须常驻，Esc 必须能从工具台返回。
/// 这两条曾经让用户「用了一个工具就回不到工具墙」。
/// </summary>
public sealed class WorkspaceWindowTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }
        public byte[]? ImagePng { get; set; }
        public string? GetText() => Text;
        public byte[]? GetImagePng() => ImagePng;
        public bool SetText(string text)
        {
            Text = text;
            return true;
        }
        public bool SetImagePng(byte[] png)
        {
            ImagePng = png;
            return true;
        }
    }

    private static (WorkspaceWindow Window, WorkspaceViewModel ViewModel, ToolCatalog Catalog) Create()
    {
        var log = new NullLog();
        var rootPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        var catalog = new ToolCatalog(rootPath, log, new AppSettings());
        var store = new SettingsStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N") + ".json"),
            log);
        var settingsViewModel = new SettingsViewModel(catalog, store, new StartupService(log), new AppSettings());

        var viewModel = new WorkspaceViewModel(catalog, new FakeClipboard(), log, new NullHistoryStore(), settingsViewModel);
        var window = new WorkspaceWindow { DataContext = viewModel };
        window.Show();

        return (window, viewModel, catalog);
    }

    [AvaloniaFact]
    public void 最近页_切换tab行仍然可见()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.ShowRecent();

            var tabRow = window.FindControl<Grid>("WallTabRow");
            tabRow.ShouldNotBeNull("最近页必须保留「工具 / 最近使用」切换入口");
            tabRow.IsVisible.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 工具台_Esc返回工具墙()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            viewModel.IsDetailOpen.ShouldBeFalse();
            viewModel.IsWallVisible.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [AvaloniaFact]
    public void 设置页_Esc不抢返回()
    {
        var (window, viewModel, catalog) = Create();
        try
        {
            viewModel.IsDetailOpen = true;
            viewModel.IsSettingsVisible = true;

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            viewModel.IsDetailOpen.ShouldBeTrue();
        }
        finally
        {
            catalog.Dispose();
        }
    }
}
