using Avalonia.Controls;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Ui;

namespace Sparrow.Plugin.UiSpike;

/// <summary>
/// P4-A 验收用的测试插件：实现 <see cref="IToolViewFactory"/>，
/// 用宿主提供的 <c>PluginSection</c> / <c>PluginHint</c> / <c>PluginNumberField</c>
/// 组合出界面，证明「宿主控件组合」这条路能跨 ALC 走通。
/// </summary>
public sealed class SpikeTool : ITool, IToolViewFactory
{
    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "spike.ui.view",
        Name = "UI Spike",
        Category = "test",
        Description = "仅用于验证插件界面跨 ALC 加载",
        Accepts = InputShape.None,
        Produces = OutputShape.Text
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ToolResult.Ok(new TextPayload("ui spike ok")));

    /// <summary>只会在 UI 线程由宿主调用；返回 null 表示用宿主默认工具台。</summary>
    public Control? CreateView(IToolViewContext context) => new SpikeView();
}
