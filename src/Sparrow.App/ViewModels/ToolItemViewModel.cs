using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 列表项的只读视图模型。
/// 不可变，所以不需要属性通知——搜索结果是整体替换而不是逐项修改。
/// </summary>
public sealed class ToolItemViewModel
{
    public ToolItemViewModel(ITool tool) => Tool = tool;

    public ITool Tool { get; }

    public string Name => Tool.Descriptor.Name;

    public string Description => Tool.Descriptor.Description;

    public string CategoryLabel => Tool.Descriptor.Category;
}
