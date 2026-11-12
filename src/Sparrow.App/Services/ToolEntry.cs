using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 注册表里的一个工具：实例 + 它的宿主能力。
///
/// 把两者绑在一起，是因为 <c>ToolContext.Host</c> 必须指向**这个插件自己的**
/// 目录和日志前缀——OCR 的 worker 路径就是从 Host.PluginDirectory 算出来的。
/// </summary>
public sealed class ToolEntry
{
    public required ITool Tool { get; init; }

    public required IToolHost Host { get; init; }

    public ToolDescriptor Descriptor => Tool.Descriptor;
}
