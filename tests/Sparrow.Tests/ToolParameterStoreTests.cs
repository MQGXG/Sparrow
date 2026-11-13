using Shouldly;
using Sparrow.App;
using Sparrow.App.Settings;
using Xunit;

namespace Sparrow.Tests;

/// <summary>工具参数的持久化：重启后仍记住上次选择。</summary>
public sealed class ToolParameterStoreTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static string TempPath() => Path.Combine(
        Path.GetTempPath(),
        "sparrow-tests",
        Guid.NewGuid().ToString("N"),
        "parameters.json");

    [Fact]
    public void 保存后重启仍能读到()
    {
        var path = TempPath();

        var first = new ToolParameterStore(path, new NullLog());
        first.Save("sparrow.ocr.image", new Dictionary<string, string>
        {
            ["language"] = "en"
        });

        var second = new ToolParameterStore(path, new NullLog());

        second.GetValue("sparrow.ocr.image", "language").ShouldBe("en");
    }

    [Fact]
    public void 未保存的工具_返回空字典()
    {
        var store = new ToolParameterStore(TempPath(), new NullLog());

        store.Get("sparrow.unknown").ShouldBeEmpty();
    }

    [Fact]
    public void 损坏的文件_回退为空()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ broken json");

        var store = new ToolParameterStore(path, new NullLog());

        store.GetValue("sparrow.ocr.image", "language").ShouldBeNull();
    }

    [Fact]
    public void 覆盖保存_只保留最新值()
    {
        var path = TempPath();
        var store = new ToolParameterStore(path, new NullLog());

        store.Save("sparrow.ocr.image", new Dictionary<string, string> { ["language"] = "ch" });
        store.Save("sparrow.ocr.image", new Dictionary<string, string> { ["language"] = "en" });

        var restarted = new ToolParameterStore(path, new NullLog());
        restarted.GetValue("sparrow.ocr.image", "language").ShouldBe("en");
    }
}
