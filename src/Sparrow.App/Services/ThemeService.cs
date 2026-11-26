using Avalonia;
using Avalonia.Styling;

namespace Sparrow.App.Services;

/// <summary>
/// 外观三态的单一入口：跟随系统 / 浅色 / 深色。
///
/// 颜色 token 在 App.axaml 的 ThemeDictionaries 里各有一套，界面全部用
/// DynamicResource 引用，所以改 Application.RequestedThemeVariant 即时生效。
/// 固定浅色 / 深色时不会跟随系统；只有 system 映射到 ThemeVariant.Default，
/// 由 Avalonia 订阅平台主题变化。
///
/// 截图 / 贴图 / 灯箱等「暗房」窗口在 XAML 里写死 RequestedThemeVariant="Dark"，
/// 不参与切换，保证看图和取景时的对比度恒定。
/// </summary>
public static class ThemeService
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>未知值一律回落到深色，默认外观与历史版本一致。</summary>
    public static string Normalize(string? appearance) => appearance switch
    {
        Light => Light,
        System => System,
        _ => Dark,
    };

    /// <summary>把设置值映射到 Avalonia 的主题变体并应用到整个应用。</summary>
    public static void Apply(string? appearance)
    {
        if (Application.Current is not { } app)
            return;

        var variant = Normalize(appearance) switch
        {
            System => ThemeVariant.Default,
            Light => ThemeVariant.Light,
            _ => ThemeVariant.Dark,
        };

        if (!Equals(app.RequestedThemeVariant, variant))
            app.RequestedThemeVariant = variant;
    }

    public static bool IsDark(ThemeVariant? variant) => variant != ThemeVariant.Light;

    /// <summary>当前实际是不是深色（system 模式要看平台实际主题）。</summary>
    public static bool IsDarkAppearance(string? appearance, ThemeVariant? actual) =>
        Normalize(appearance) switch
        {
            Light => false,
            System => IsDark(actual),
            _ => true,
        };

    /// <summary>顶栏快捷按钮：在当前观感的深 / 浅之间切换，system 会被收敛成固定模式。</summary>
    public static string Toggle(string? appearance, ThemeVariant? actual) =>
        IsDarkAppearance(appearance, actual) ? Light : Dark;
}
