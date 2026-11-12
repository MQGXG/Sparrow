using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Sparrow.Plugin.Json.Format;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 面板键盘路由的确定性问题。
///
/// 为什么不用 Win32 注入按键验证：keybd_event 受前台焦点竞争影响，
/// 同一份代码两次跑结果不同，没法作为回归证据。
/// Headless 平台把原始输入直接送进 Avalonia 路由，能稳定复现
/// 「TextBox 先吃掉 Enter/Ctrl+C」这类问题。
/// </summary>
public sealed class PaletteWindowTests
{
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
    }

    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static (PaletteWindow Window, PaletteViewModel ViewModel, FakeClipboard Clipboard) Create()
    {
        var clipboard = new FakeClipboard { Text = """{"a":1}""" };
        var viewModel = new PaletteViewModel(
            () => ToolRegistry.FromTools([new JsonFormatTool()]),
            clipboard,
            new NullLog());

        viewModel.Query = "json";

        var window = new PaletteWindow { DataContext = viewModel };
        window.Show();

        return (window, viewModel, clipboard);
    }

    [AvaloniaFact]
    public void Enter_执行当前工具()
    {
        var (window, viewModel, _) = Create();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

        viewModel.HasResult.ShouldBeTrue();
        viewModel.ResultText!.ShouldContain("\"a\": 1");
    }

    [AvaloniaFact]
    public void CtrlC_复制结果到剪贴板()
    {
        var (window, viewModel, clipboard) = Create();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.None, null);

        clipboard.Text.ShouldBe(viewModel.ResultText);
    }

    [AvaloniaFact]
    public void Esc_隐藏窗口()
    {
        var (window, _, _) = Create();
        window.IsVisible.ShouldBeTrue();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        window.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Enter_没有选中工具时不抛异常()
    {
        var clipboard = new FakeClipboard { Text = "{}" };
        var viewModel = new PaletteViewModel(
            () => ToolRegistry.FromTools([]),
            clipboard,
            new NullLog());

        // 不设置查询：列表为空，Selected 为 null
        var window = new PaletteWindow { DataContext = viewModel };
        window.Show();

        Should.NotThrow(() => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null));
    }

    /// <summary>
    /// 中文输入法下，第一次 Enter 的职责是「把组合串上屏」，不是执行工具。
    /// 抢了它会导致：先执行、随后上屏触发搜索刷新、结果被清空。
    /// 回归保护——这条曾经在真机上让整个面板不可用。
    /// </summary>
    [AvaloniaFact]
    public void ImeProcessed的Enter_不执行工具也不复制()
    {
        var (window, viewModel, clipboard) = Create();
        var before = clipboard.Text;

        window.KeyPress(Key.ImeProcessed, RawInputModifiers.None, PhysicalKey.Enter, null);

        viewModel.HasResult.ShouldBeFalse();
        viewModel.HasError.ShouldBeFalse();
        clipboard.Text.ShouldBe(before);
    }
}
