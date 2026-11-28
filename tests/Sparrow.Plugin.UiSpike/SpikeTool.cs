using Avalonia.Controls;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.UiSpike;

/// <summary>
/// 只用于 P3 验证：宿主通过反射调用 <see cref="CreateView"/>，
/// 确认插件程序集里的 <see cref="Control"/> 能被宿主直接显示。
/// P4 会把这条路径固化成 IToolViewFactory。
/// </summary>
public sealed class SpikeTool : ITool
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

    public Control CreateView() => new SpikeView();
}
