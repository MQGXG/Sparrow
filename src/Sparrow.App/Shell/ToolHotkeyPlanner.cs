using Sparrow.App.Services;
using Sparrow.App.Settings;

namespace Sparrow.App.Shell;

/// <summary>一条可注册的工具热键：工具 ID + 解析后的按键。</summary>
public sealed record ToolHotkeyPlan(
    string ToolId,
    string HotkeyText,
    uint Modifiers,
    uint VirtualKey,
    int Id);

/// <summary>
/// 把 settings.json 的 <c>toolHotkeys</c> 转成热键注册计划。
///
/// 纯函数：格式错误的条目跳过并记日志，绝不让一条手误拖垮应用启动。
/// ID 从 <see cref="FirstId"/> 递增，与搜索/截图热键的固定 ID 不相交。
/// </summary>
public static class ToolHotkeyPlanner
{
    public const int FirstId = 0x5A60;

    public static IReadOnlyList<ToolHotkeyPlan> Build(
        IReadOnlyDictionary<string, string>? toolHotkeys,
        IAppLog log)
    {
        if (toolHotkeys is null || toolHotkeys.Count == 0)
            return [];

        var plans = new List<ToolHotkeyPlan>(toolHotkeys.Count);
        var id = FirstId;

        foreach (var (toolId, hotkeyText) in toolHotkeys)
        {
            if (string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(hotkeyText))
                continue;

            if (!HotkeyText.TryParse(hotkeyText, out var modifiers, out var virtualKey))
            {
                log.Warn($"工具热键格式无效，已跳过：{toolId} = {hotkeyText}");
                continue;
            }

            plans.Add(new ToolHotkeyPlan(toolId.Trim(), hotkeyText.Trim(), modifiers, virtualKey, id));
            id++;
        }

        return plans;
    }
}
