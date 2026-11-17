using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Overlays;
using Xunit;

namespace Sparrow.Tests;

/// <summary>应用内 toast：内容正确、可点击关闭、定时自动消失。</summary>
public sealed class ToastWindowTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [AvaloniaFact]
    public void Toast_显示消息并可手动关闭()
    {
        var toast = new ToastWindow("热键被占用：窗口置顶 Ctrl+Alt+P", TimeSpan.FromMinutes(1));

        toast.Show();

        toast.IsVisible.ShouldBeTrue();
        toast.FindControl<TextBlock>("MessageText")!.Text
            .ShouldBe("热键被占用：窗口置顶 Ctrl+Alt+P");

        toast.Close();
        toast.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Toast_到时自动关闭()
    {
        var toast = new ToastWindow("自动消失", TimeSpan.FromMilliseconds(80));

        toast.Show();
        toast.IsVisible.ShouldBeTrue();

        await Task.Delay(500);

        toast.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void ToastService_连续提示不抛异常()
    {
        var service = new ToastService(new NullLog());

        Should.NotThrow(() =>
        {
            service.Show("第一条", TimeSpan.FromMinutes(1));
            service.Show("第二条", TimeSpan.FromMinutes(1));
            service.Dispose();
        });
    }
}
