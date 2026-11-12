using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 列表项的只读视图模型。
/// 不可变，所以不需要属性通知——搜索结果是整体替换而不是逐项修改。
/// </summary>
public sealed class ToolItemViewModel
{
    public ToolItemViewModel(ToolEntry entry) => Entry = entry;

    public ToolEntry Entry { get; }

    public ITool Tool => Entry.Tool;

    public string Name => Entry.Descriptor.Name;

    public string Description => Entry.Descriptor.Description;

    public string CategoryLabel => Entry.Descriptor.Category;
}
