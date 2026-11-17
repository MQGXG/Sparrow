using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sparrow.App.Overlays;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 覆盖窗的加载与生命周期：XAML 能实例化、取消时返回 0、目标不存在时自动关闭。
/// 真实的拾取命中依赖 Win32，属于手动验收范围。
/// </summary>
public sealed class OverlayWindowTests
{
    [AvaloniaFact]
    public void 拾取窗_XAML能加载并设置提示文案()
    {
        var window = new WindowPickerWindow("点击窗口置顶 · 右键或 Esc 取消");

        var hint = window.FindControl<TextBlock>("HintText");
        hint.ShouldNotBeNull();
        hint!.Text.ShouldBe("点击窗口置顶 · 右键或 Esc 取消");
    }

    [AvaloniaFact]
    public async Task 拾取窗_关闭时返回0()
    {
        var window = new WindowPickerWindow("test");

        var picking = window.PickAsync(CancellationToken.None);
        window.Close();

        (await picking).ShouldBe((nint)0);
    }

    [AvaloniaFact]
    public void 图钉窗_目标不存在时自动关闭()
    {
        var window = new PinIndicatorWindow(0, () => { });

        window.Show();

        window.IsVisible.ShouldBeFalse();
    }
}
