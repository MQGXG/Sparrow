using Sparrow.App.Services;
using Sparrow.App.Settings;

namespace Sparrow.App.Shell;

/// <summary>托盘菜单项类型。</summary>
public enum TrayMenuActionKind
{
    Palette,
    FileSearch,
    Clipboard,
    Capture,
    Record,
    LanShare,
    Workspace,
    Exit,
    Tool,
    Separator
}

/// <summary>一条可渲染的托盘菜单项。</summary>
public sealed record TrayMenuEntry(TrayMenuActionKind Kind, string Label, string? ToolId = null);

/// <summary>
/// 把 settings.json 的 <c>trayMenu</c> 转成菜单条目；空配置时生成内置默认菜单。
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
        IReadOnlyList<string> defaultToolIds,
        string searchHotkeyText,
        string captureHotkeyText,
        string clipboardHotkeyText,
        IAppLog log)
    {
        if (config is not null && config.Count > 0)
        {
            return BuildEntries(
                config,
                toolHotkeys,
                toolNames,
                searchHotkeyText,
                captureHotkeyText,
                clipboardHotkeyText,
                log);
        }

        // 默认菜单：宿主动作 + 插件声明「默认进托盘」的动作工具。
        // 不再把「配了热键的工具」自动追加进来——热键本身就是入口。
        var defaults = BuildDefaultConfig(defaultToolIds);
        defaults.RemoveAll(item => item.Tool is not null && !toolNames.ContainsKey(item.Tool));

        return BuildEntries(
            defaults,
            toolHotkeys,
            toolNames,
            searchHotkeyText,
            captureHotkeyText,
            clipboardHotkeyText,
            log);
    }

    /// <summary>
    /// 内置默认托盘菜单的显式配置。
    /// 升级迁移也用它：一次性写进 settings.json，之后菜单只由配置决定。
    /// </summary>
    public static List<TrayMenuItemConfig> BuildDefaultConfig(
        IReadOnlyList<string> defaultToolIds,
        IEnumerable<string>? extraToolIds = null)
    {
        var items = new List<TrayMenuItemConfig>
        {
            new() { Action = "palette" },
            new() { Action = "filesearch" },
            new() { Action = "clipboard" },
            new() { Action = "capture" },
            new() { Action = "record" },
            new() { Action = "lanshare" },
            new() { Action = "workspace" }
        };

        foreach (var toolId in defaultToolIds)
            items.Add(new TrayMenuItemConfig { Tool = toolId });

        if (extraToolIds is not null)
        {
            foreach (var toolId in extraToolIds)
            {
                if (defaultToolIds.Contains(toolId, StringComparer.Ordinal))
                    continue;

                items.Add(new TrayMenuItemConfig { Tool = toolId });
            }
        }

        items.Add(new TrayMenuItemConfig { Separator = true });
        items.Add(new TrayMenuItemConfig { Action = "exit" });

        return items;
    }

    private static IReadOnlyList<TrayMenuEntry> BuildEntries(
        IReadOnlyList<TrayMenuItemConfig> config,
        IReadOnlyDictionary<string, string> toolHotkeys,
        IReadOnlyDictionary<string, string> toolNames,
        string searchHotkeyText,
        string captureHotkeyText,
        string clipboardHotkeyText,
        IAppLog log)
    {
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

                case "filesearch":
                    entries.Add(new TrayMenuEntry(
                        TrayMenuActionKind.FileSearch,
                        item.Label?.Trim() is { Length: > 0 } fileSearchLabel
                            ? fileSearchLabel
                            : "文件搜索…    双击 Ctrl"));
                    break;

                case "clipboard":
                    entries.Add(new TrayMenuEntry(
                        TrayMenuActionKind.Clipboard,
                        item.Label?.Trim() is { Length: > 0 } clipboardLabel
                            ? clipboardLabel
                            : $"剪贴板面板…    {clipboardHotkeyText}"));
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

                case "lanshare":
                    entries.Add(new TrayMenuEntry(
                        TrayMenuActionKind.LanShare,
                        item.Label?.Trim() is { Length: > 0 } lanShareLabel ? lanShareLabel : "局域网互传…"));
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
}
