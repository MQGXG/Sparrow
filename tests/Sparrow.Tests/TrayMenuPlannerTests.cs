using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.Shell;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 托盘菜单配置解析：默认菜单与内置行为一致；自定义顺序 / 动作 / 工具项按配置生成；
/// 非法条目跳过而不是让菜单起不来。
/// </summary>
public sealed class TrayMenuPlannerTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static readonly Dictionary<string, string> ToolHotkeys = new(StringComparer.Ordinal)
    {
        ["sparrow.window.pin"] = "Ctrl+Alt+P"
    };

    private static readonly Dictionary<string, string> ToolNames = new(StringComparer.Ordinal)
    {
        ["sparrow.window.pin"] = "窗口置顶"
    };

    private static IReadOnlyList<TrayMenuEntry> Build(IReadOnlyList<TrayMenuItemConfig>? config) =>
        TrayMenuPlanner.Build(
            config,
            ToolHotkeys,
            ToolNames,
            "Ctrl+Alt+S",
            "Ctrl+Alt+C",
            new NullLog());

    [Fact]
    public void 空配置_生成默认菜单()
    {
        var entries = Build(null);

        entries.Select(entry => entry.Kind).ShouldBe(
        [
            TrayMenuActionKind.Palette,
            TrayMenuActionKind.Capture,
            TrayMenuActionKind.Record,
            TrayMenuActionKind.Workspace,
            TrayMenuActionKind.Tool,
            TrayMenuActionKind.Separator,
            TrayMenuActionKind.Exit
        ]);

        entries[0].Label.ShouldContain("Ctrl+Alt+S");
        entries[1].Label.ShouldContain("Ctrl+Alt+C");
        entries[2].Label.ShouldContain("Ctrl+Alt+Shift+R");
        entries[4].Label.ShouldBe("窗口置顶…    Ctrl+Alt+P");
    }

    [Fact]
    public void 自定义配置_保持顺序与动作()
    {
        var entries = Build(
        [
            new TrayMenuItemConfig { Separator = true },
            new TrayMenuItemConfig { Action = "workspace", Label = "打开工作区" },
            new TrayMenuItemConfig { Tool = "sparrow.window.pin" },
            new TrayMenuItemConfig { Separator = true },
            new TrayMenuItemConfig { Action = "exit", Label = "关闭" }
        ]);

        entries.Select(entry => entry.Kind).ShouldBe(
        [
            TrayMenuActionKind.Separator,
            TrayMenuActionKind.Workspace,
            TrayMenuActionKind.Tool,
            TrayMenuActionKind.Separator,
            TrayMenuActionKind.Exit
        ]);

        entries[1].Label.ShouldBe("打开工作区");
        entries[2].Label.ShouldBe("窗口置顶…    Ctrl+Alt+P");
        entries[2].ToolId.ShouldBe("sparrow.window.pin");
        entries[4].Label.ShouldBe("关闭");
    }

    [Fact]
    public void 工具项_没有清单名称时退回工具ID()
    {
        var entries = TrayMenuPlanner.Build(
            [new TrayMenuItemConfig { Tool = "unknown.tool" }],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            "Ctrl+Alt+S",
            "Ctrl+Alt+C",
            new NullLog());

        var entry = entries.ShouldHaveSingleItem();
        entry.Kind.ShouldBe(TrayMenuActionKind.Tool);
        entry.Label.ShouldBe("unknown.tool");
        entry.ToolId.ShouldBe("unknown.tool");
    }

    [Fact]
    public void 非法动作_跳过()
    {
        var entries = Build(
        [
            new TrayMenuItemConfig { Action = "dosomething" },
            new TrayMenuItemConfig { Action = "exit" }
        ]);

        entries.ShouldHaveSingleItem().Kind.ShouldBe(TrayMenuActionKind.Exit);
    }
}
