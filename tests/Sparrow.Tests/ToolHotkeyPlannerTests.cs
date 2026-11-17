using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.Shell;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 工具热键配置解析：合法条目转成注册计划，坏条目只跳过、不影响启动。
/// </summary>
public sealed class ToolHotkeyPlannerTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public void 未配置_返回空计划()
    {
        ToolHotkeyPlanner.Build(null, new NullLog()).ShouldBeEmpty();
        ToolHotkeyPlanner.Build(new Dictionary<string, string>(), new NullLog()).ShouldBeEmpty();
    }

    [Fact]
    public void 有效配置_解析修饰符与按键()
    {
        var plans = ToolHotkeyPlanner.Build(
            new Dictionary<string, string> { ["sparrow.window.pin"] = "Ctrl+Alt+P" },
            new NullLog());

        var plan = plans.ShouldHaveSingleItem();
        plan.ToolId.ShouldBe("sparrow.window.pin");
        plan.HotkeyText.ShouldBe("Ctrl+Alt+P");
        plan.Modifiers.ShouldBe(HotkeyService.ModControl | HotkeyService.ModAlt);
        plan.VirtualKey.ShouldBe((uint)'P');
        plan.Id.ShouldBe(ToolHotkeyPlanner.FirstId);
    }

    [Fact]
    public void 格式错误_跳过该条()
    {
        var plans = ToolHotkeyPlanner.Build(
            new Dictionary<string, string>
            {
                ["sparrow.window.pin"] = "NotAKey",
                ["sparrow.json.format"] = "Ctrl+Alt+J"
            },
            new NullLog());

        plans.ShouldHaveSingleItem().ToolId.ShouldBe("sparrow.json.format");
    }

    [Fact]
    public void 空键或空值_跳过()
    {
        var plans = ToolHotkeyPlanner.Build(
            new Dictionary<string, string>
            {
                [""] = "Ctrl+Alt+P",
                ["sparrow.window.pin"] = "  "
            },
            new NullLog());

        plans.ShouldBeEmpty();
    }

    [Fact]
    public void 多条配置_ID顺序递增()
    {
        var plans = ToolHotkeyPlanner.Build(
            new Dictionary<string, string>
            {
                ["sparrow.a.one"] = "Ctrl+Alt+1",
                ["sparrow.b.two"] = "Ctrl+Alt+2"
            },
            new NullLog());

        plans.Count.ShouldBe(2);
        plans[0].Id.ShouldBe(ToolHotkeyPlanner.FirstId);
        plans[1].Id.ShouldBe(ToolHotkeyPlanner.FirstId + 1);
    }

    [Fact]
    public void settings里的工具热键_能落盘并读回()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"), "settings.json");

        try
        {
            var store = new SettingsStore(path, new NullLog());
            store.Save(new AppSettings
            {
                ToolHotkeys = new Dictionary<string, string> { ["sparrow.window.pin"] = "Ctrl+Alt+P" }
            });

            var loaded = store.Load();

            loaded.ToolHotkeys.ShouldContainKeyAndValue("sparrow.window.pin", "Ctrl+Alt+P");
        }
        finally
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
