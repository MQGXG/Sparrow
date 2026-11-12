using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Shouldly;
using Sparrow.App.Shell;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 托盘图标的注册。
///
/// Avalonia 12 的 TrayIcon 构造后不会自动挂到应用上，
/// 必须放进 Application 的 TrayIcons 集合才会 Attach。
/// 这条曾经导致「进程正常、日志正常、托盘里什么都没有」。
/// </summary>
public sealed class TrayServiceTests
{
    [AvaloniaFact]
    public void 初始化后_图标进入应用的TrayIcons集合()
    {
        var palette = new PaletteWindow();
        var service = new TrayService(palette, () => { }, () => { });

        service.Initialize();

        var icons = TrayIcon.GetIcons(Application.Current!);

        icons.ShouldNotBeNull();
        icons!.Count.ShouldBe(1);
        icons[0].ToolTipText.ShouldBe("Sparrow · 麻雀");
    }
}
