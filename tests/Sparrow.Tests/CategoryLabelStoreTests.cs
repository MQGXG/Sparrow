using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// categories.json 只负责「分类显示名」的覆盖：
/// 自定义的生效、没配置的用内置名、未知分类原样显示、坏文件回退默认。
/// ToolCategories 是静态缓存，每个用例结束必须 Reset，避免跨用例串味。
/// </summary>
public sealed class CategoryLabelStoreTests : IDisposable
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private readonly string _directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));

    public CategoryLabelStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        ToolCategories.Reset();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 临时目录留给系统清理
        }
    }

    private string WriteConfig(string json)
    {
        var path = System.IO.Path.Combine(_directory, "categories.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void 没有配置文件_使用内置显示名()
    {
        ToolCategories.Reset();
        var store = new CategoryLabelStore(
            System.IO.Path.Combine(_directory, "categories.json"), new NullLog());

        ToolCategories.Configure(store.Load());

        ToolCategories.Display("json").ShouldBe("JSON");
        ToolCategories.Display("ai").ShouldBe("AI");
    }

    [Fact]
    public void 自定义显示名_覆盖内置名_未配置的保持内置()
    {
        var path = WriteConfig("""
            {
              "labels": {
                "json": "JSON 处理",
                "dev": "编码工具"
              }
            }
            """);

        var store = new CategoryLabelStore(path, new NullLog());
        ToolCategories.Configure(store.Load());

        ToolCategories.Display("json").ShouldBe("JSON 处理");
        ToolCategories.Display("dev").ShouldBe("编码工具");
        ToolCategories.Display("ai").ShouldBe("AI");
    }

    [Fact]
    public void 未知分类_原样显示()
    {
        var path = WriteConfig("""{ "labels": { "json": "JSON 处理" } }""");

        var store = new CategoryLabelStore(path, new NullLog());
        ToolCategories.Configure(store.Load());

        ToolCategories.Display("third-party").ShouldBe("third-party");
    }

    [Fact]
    public void 空键和空值_被忽略()
    {
        var path = WriteConfig("""
            { "labels": { "json": "JSON 处理", "": "空键", "ai": "  " } }
            """);

        var store = new CategoryLabelStore(path, new NullLog());
        ToolCategories.Configure(store.Load());

        ToolCategories.Display("json").ShouldBe("JSON 处理");
        ToolCategories.Display("ai").ShouldBe("AI");
    }

    [Fact]
    public void 坏文件_回退内置显示名()
    {
        var path = WriteConfig("{ 这不是 JSON");

        var store = new CategoryLabelStore(path, new NullLog());
        Should.NotThrow(() => ToolCategories.Configure(store.Load()));

        ToolCategories.Display("json").ShouldBe("JSON");
    }

    [Fact]
    public void Reset后_恢复内置显示名()
    {
        var path = WriteConfig("""{ "labels": { "json": "JSON 处理" } }""");

        var store = new CategoryLabelStore(path, new NullLog());
        ToolCategories.Configure(store.Load());
        ToolCategories.Display("json").ShouldBe("JSON 处理");

        ToolCategories.Reset();

        ToolCategories.Display("json").ShouldBe("JSON");
    }
}
