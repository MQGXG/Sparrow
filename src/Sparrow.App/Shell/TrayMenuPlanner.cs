using Sparrow.App.Services;
using Sparrow.App.Settings;

namespace Sparrow.App.Shell;

/// <summary>托盘菜单项类型。</summary>
public enum TrayMenuActionKind
{
    Palette,
    Capture,
    Record,
    Workspace,
    Exit,
    Tool,
    Separator
}

/// <summary>一条可渲染的托盘菜单项。</summary>
public sealed record TrayMenuEntry(TrayMenuActionKind Kind, string Label, string? ToolId = null);

/// <summary>
/// 把 settings.json 的 <c>trayMenu</c> 转成菜单条目；空配置时生成与内置行为一致的默认菜单。
///
/// 纯函数：非法条目跳过并记日志，菜单永远能起来；
/// 工具项的名称只依赖插件清单（toolId → 名称），不加载插件程序集。
/// </summary>
public static class TrayMenuPlanner
{
    public static IReadOnlyList<TrayMenuEntry> Build(
        IReadOnlyList<TrayMenuItemConfig>? config,
        IReadOnlyDictionary<string, string> toolHotkeys,
        IReadOnlyDictionary<string, string> toolNames,
        string searchHotkeyText,
        string captureHotkeyText,
        IAppLog log)
    {
        var toolEntries = BuildToolEntries(toolHotkeys, toolNames);

        if (config is null || config.Count == 0)
        {
            var defaults = new List<TrayMenuEntry>
            {
                new(TrayMenuActionKind.Palette, $"搜索工具…    {searchHotkeyText}"),
                new(TrayMenuActionKind.Capture, $"截图识别…    {captureHotkeyText}"),
                new(TrayMenuActionKind.Record, "屏幕录制…    Ctrl+Alt+Shift+R"),
                new(TrayMenuActionKind.Workspace, "工作区…")
            };

            defaults.AddRange(toolEntries);
            defaults.Add(new TrayMenuEntry(TrayMenuActionKind.Separator, string.Empty));
            defaults.Add(new TrayMenuEntry(TrayMenuActionKind.Exit, "退出"));

            return defaults;
        }

        var entries = new List<TrayMenuEntry>(config.Count);

        foreach (var item in config)
        {
            if (item.Separator)
            {
                entries.Add(new TrayMenuEntry(TrayMenuActionKind.Separator, string.Empty));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Tool))
            {
                var toolId = item.Tool.Trim();
                var label = item.Label?.Trim();
                if (string.IsNullOrEmpty(label))
                    label = toolNames.TryGetValue(toolId, out var name) ? name : toolId;

                if (toolHotkeys.TryGetValue(toolId, out var hotkey) && !string.IsNullOrWhiteSpace(hotkey))
                    label = $"{label}…    {hotkey}";

                entries.Add(new TrayMenuEntry(TrayMenuActionKind.Tool, label, toolId));
                continue;
            }

            switch (item.Action?.Trim().ToLowerInvariant())
            {
                case "palette":
                    entries.Add(new TrayMenuEntry(
                        TrayMenuActionKind.Palette,
                        item.Label?.Trim() is { Length: > 0 } paletteLabel
                            ? paletteLabel
                            : $"搜索工具…    {searchHotkeyText}"));
                    break;

                case "capture":
                    entries.Add(new TrayMenuEntry(
                        TrayMenuActionKind.Capture,
                        item.Label?.Trim() is { Length: > 0 } captureLabel
                            ? captureLabel
                            : $"截图识别…    {captureHotkeyText}"));
                    break;

                case "record":
                    entries.Add(new TrayMenuEntry(
                        TrayMenuActionKind.Record,
                        item.Label?.Trim() is { Length: > 0 } recordLabel
                            ? recordLabel
                            : "屏幕录制…    Ctrl+Alt+Shift+R"));
                    break;

                case "workspace":
                    entries.Add(new TrayMenuEntry(
                        TrayMenuActionKind.Workspace,
                        item.Label?.Trim() is { Length: > 0 } workspaceLabel ? workspaceLabel : "工作区…"));
                    break;

                case "exit":
                    entries.Add(new TrayMenuEntry(
                        TrayMenuActionKind.Exit,
                        item.Label?.Trim() is { Length: > 0 } exitLabel ? exitLabel : "退出"));
                    break;

                default:
                    log.Warn($"托盘菜单条目无效，已跳过：label={item.Label}，action={item.Action}");
                    break;
            }
        }

        return entries;
    }

    private static IEnumerable<TrayMenuEntry> BuildToolEntries(
        IReadOnlyDictionary<string, string> toolHotkeys,
        IReadOnlyDictionary<string, string> toolNames)
    {
        foreach (var (toolId, hotkey) in toolHotkeys)
        {
            var name = toolNames.TryGetValue(toolId, out var displayName) ? displayName : toolId;
            yield return new TrayMenuEntry(
                TrayMenuActionKind.Tool,
                $"{name}…    {hotkey}",
                toolId);
        }
    }
}
