namespace Sparrow.App.Settings;

/// <summary>
/// 一条托盘菜单配置。三种写法：
/// <list type="bullet">
/// <item><c>{ "separator": true }</c> —— 分隔线</item>
/// <item><c>{ "action": "palette" }</c> —— 内置动作为 palette / capture / workspace / exit</item>
/// <item><c>{ "tool": "sparrow.window.pin" }</c> —— 执行某个工具（已配置热键时自动显示在标签后）</item>
/// </list>
/// 两项都不填的条目会被跳过并记日志，不影响其余菜单。
/// </summary>
public sealed record TrayMenuItemConfig
{
    public bool Separator { get; init; }

    /// <summary>菜单文字；留空时使用默认文案或工具名。</summary>
    public string? Label { get; init; }

    /// <summary>内置动作：palette / capture / workspace / exit。</summary>
    public string? Action { get; init; }

    /// <summary>工具 ID；与 <see cref="Action"/> 二选一。</summary>
    public string? Tool { get; init; }
}
