using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Sparrow.Tests.TestAppBuilder))]

namespace Sparrow.Tests;

/// <summary>
/// 无头测试宿主。
/// 只加载 Fluent 主题，不创建托盘 / 热键 / 插件目录——
/// 这些在无头环境里没有意义，也会让测试依赖真实 Windows 桌面。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // 关掉「无绘制」模式：需要真实渲染时（截图 / 字体度量）才有像素
                UseHeadlessDrawing = false
            });
}

public sealed class TestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}
