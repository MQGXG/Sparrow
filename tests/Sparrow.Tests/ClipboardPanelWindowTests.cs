using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>热键呼出的剪贴板面板：只做搜索 + 列表，回车 / 双击复制后收起。</summary>
public sealed class ClipboardPanelWindowTests
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

    [AvaloniaFact]
    public void 剪贴板面板_回车复制并收起()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("第一条");

            var clipboard = new FakeClipboard();
            var viewModel = new ClipboardHistoryViewModel(store, clipboard);
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

            clipboard.Text.ShouldBe("第一条");
            window.IsVisible.ShouldBeFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    [AvaloniaFact]
    public void 剪贴板面板_查看详情覆盖在面板上_Esc回到列表()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"),
                new NullLog());
            store.AddText("第一条");

            var viewModel = new ClipboardHistoryViewModel(store, new FakeClipboard());
            var window = new ClipboardPanelWindow { DataContext = viewModel };
            window.Show();
            window.PrepareForShow();
            Dispatcher.UIThread.RunJobs();

            var viewButton = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button => Equals(button.Content, "查看"));
            viewButton.ShouldNotBeNull("列表行必须提供「查看」入口");
            viewButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var layer = window.FindControl<Panel>("DetailLayer");
            layer.ShouldNotBeNull();
            layer!.IsVisible.ShouldBeTrue("详情应该覆盖在面板内，而不是另开窗口");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            layer.IsVisible.ShouldBeFalse();
            window.IsVisible.ShouldBeTrue("Esc 只返回列表，不应收起面板");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }
}
