using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;

namespace Sparrow.Tests;

/// <summary>
/// 双主题回归：颜色 token 定义在 App.axaml 的 ThemeDictionaries 里，
/// Dark / Light 必须各有一份完整取值，否则界面切主题时会留下深色块或空白。
/// </summary>
public sealed class ThemeTokensTests
{
    private static readonly string[] BrushKeys =
    [
        "BgBrush", "SurfaceBrush", "SurfaceRaisedBrush", "SurfaceHoverBrush",
        "LineBrush", "LineStrongBrush", "OverlayBrush", "ScrimBrush",
        "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush",
        "AccentBrush", "AccentInkBrush", "AccentFillBrush", "AccentHoverBrush",
        "AccentPressedBrush", "AccentSoftBrush", "AccentBorderBrush", "AccentOnBrush",
        "AccentSelectionBrush", "DangerBrush", "DangerBgBrush", "DangerLineBrush",
        "ScrollThumbBrush", "ScrollThumbHoverBrush",
        "WindowBgGradientBrush", "SurfaceGradientBrush", "PanelGradientBrush",
        "AccentGradientBrush", "AccentGradientHoverBrush", "DotGridBrush"
    ];

    private static global::Sparrow.App.App CreateApp()
    {
        var app = new global::Sparrow.App.App();
        app.Initialize();
        return app;
    }

    [AvaloniaFact]
    public void 主题字典_深浅两套_token_都完整()
    {
        var app = CreateApp();

        foreach (var key in BrushKeys)
        {
            app.TryFindResource(key, ThemeVariant.Dark, out var dark)
                .ShouldBeTrue($"Dark 主题缺少 {key}");
            app.TryFindResource(key, ThemeVariant.Light, out var light)
                .ShouldBeTrue($"Light 主题缺少 {key}");
            dark.ShouldNotBeNull($"Dark 的 {key} 不应为 null");
            light.ShouldNotBeNull($"Light 的 {key} 不应为 null");
        }
    }

    [AvaloniaFact]
    public void 主题字典_浅色不是深色的复制()
    {
        var app = CreateApp();

        app.TryFindResource("TextPrimaryBrush", ThemeVariant.Dark, out var darkText);
        app.TryFindResource("TextPrimaryBrush", ThemeVariant.Light, out var lightText);
        ((ISolidColorBrush)darkText!).Color
            .ShouldNotBe(((ISolidColorBrush)lightText!).Color, "浅色文字必须换成深色墨");

        app.TryFindResource("BgBrush", ThemeVariant.Dark, out var darkBg);
        app.TryFindResource("BgBrush", ThemeVariant.Light, out var lightBg);
        ((ISolidColorBrush)darkBg!).Color
            .ShouldNotBe(((ISolidColorBrush)lightBg!).Color, "浅色窗口底必须换成亮色");

        // 琥珀做文字 / 图标时在浅色下降一档，保证对比度；填充色两套一致
        app.TryFindResource("AccentBrush", ThemeVariant.Dark, out var darkAccent);
        app.TryFindResource("AccentBrush", ThemeVariant.Light, out var lightAccent);
        app.TryFindResource("AccentFillBrush", ThemeVariant.Dark, out var darkFill);
        app.TryFindResource("AccentFillBrush", ThemeVariant.Light, out var lightFill);

        ((ISolidColorBrush)darkAccent!).Color
            .ShouldNotBe(((ISolidColorBrush)lightAccent!).Color);
        ((ISolidColorBrush)darkFill!).Color
            .ShouldBe(((ISolidColorBrush)lightFill!).Color);
    }

    [AvaloniaFact]
    public void 外观设置_归一化与快捷切换()
    {
        ThemeService.Normalize(null).ShouldBe(ThemeService.Dark);
        ThemeService.Normalize("unknown").ShouldBe(ThemeService.Dark);
        ThemeService.Normalize("light").ShouldBe(ThemeService.Light);
        ThemeService.Normalize("system").ShouldBe(ThemeService.System);

        ThemeService.Toggle("dark", ThemeVariant.Dark).ShouldBe(ThemeService.Light);
        ThemeService.Toggle("light", ThemeVariant.Light).ShouldBe(ThemeService.Dark);
        ThemeService.Toggle("system", ThemeVariant.Dark).ShouldBe(ThemeService.Light);
        ThemeService.Toggle("system", ThemeVariant.Light).ShouldBe(ThemeService.Dark);

        ThemeService.IsDarkAppearance("dark", ThemeVariant.Light).ShouldBeTrue("固定深色不跟随系统");
        ThemeService.IsDarkAppearance("light", ThemeVariant.Dark).ShouldBeFalse("固定浅色不跟随系统");
        ThemeService.IsDarkAppearance("system", ThemeVariant.Light).ShouldBeFalse();
        ThemeService.IsDarkAppearance("system", ThemeVariant.Dark).ShouldBeTrue();
    }
}
