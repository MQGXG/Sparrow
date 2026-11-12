using Shouldly;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 按输入形态找工具：截图入口靠它决定「谁能接这张图」。
/// 这条路径以前写死 sparrow.ocr.image，加第二个图像工具就得改宿主。
/// </summary>
public sealed class ToolRegistryTests
{
    private sealed class FakeTool(ToolDescriptor descriptor) : ITool
    {
        public ToolDescriptor Descriptor { get; } = descriptor;

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult { Success = true });
    }

    private static ToolDescriptor Descriptor(string id, string name, InputShape accepts, int order = 0) =>
        new()
        {
            Id = id,
            Name = name,
            Category = "test",
            Accepts = accepts,
            Order = order
        };

    [Fact]
    public void 只返回接受该输入形态的工具()
    {
        var registry = ToolRegistry.FromTools(
        [
            new FakeTool(Descriptor("a.image", "图像 A", InputShape.Image)),
            new FakeTool(Descriptor("a.text", "文本 A", InputShape.Text)),
            new FakeTool(Descriptor("a.files", "文件 A", InputShape.Files))
        ]);

        var found = registry.FindAccepting(InputShape.Image);

        found.Count.ShouldBe(1);
        found[0].Descriptor.Id.ShouldBe("a.image");
    }

    [Fact]
    public void 按Order降序_同分按名称排序()
    {
        var registry = ToolRegistry.FromTools(
        [
            new FakeTool(Descriptor("b.low", "低权重", InputShape.Image, order: 10)),
            new FakeTool(Descriptor("c.name", "乙", InputShape.Image, order: 50)),
            new FakeTool(Descriptor("a.name", "甲", InputShape.Image, order: 50))
        ]);

        var found = registry.FindAccepting(InputShape.Image);

        // 同为 50 时按 Ordinal 名称排序：乙（U+4E59）在甲（U+7532）前。
        // 用 Ordinal 而不是区域文化，保证换机器按钮顺序不变。
        found.Select(entry => entry.Descriptor.Id)
            .ShouldBe(["c.name", "a.name", "b.low"]);
    }

    [Fact]
    public void 没有匹配时返回空列表()
    {
        var registry = ToolRegistry.FromTools(
        [
            new FakeTool(Descriptor("a.text", "文本 A", InputShape.Text))
        ]);

        registry.FindAccepting(InputShape.Image).ShouldBeEmpty();
    }

    [Fact]
    public void InputShape为None时不返回任何工具()
    {
        var registry = ToolRegistry.FromTools(
        [
            new FakeTool(Descriptor("a.none", "无输入", InputShape.None))
        ]);

        registry.FindAccepting(InputShape.None).ShouldBeEmpty();
    }
}
