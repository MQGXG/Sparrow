using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 列表项的只读视图模型。
/// 除「是否选中」外不可变；搜索结果是整体替换而不是逐项修改。
/// </summary>
public sealed partial class ToolItemViewModel : ObservableObject
{
    public ToolItemViewModel(ToolEntry entry) => Entry = entry;

    public ToolEntry Entry { get; }

    public ITool Tool => Entry.Tool;

    public string Name => Entry.Descriptor.Name;

    public string Description => Entry.Descriptor.Description;

    public string CategoryLabel => Entry.Descriptor.Category;

    /// <summary>工作区左栏的选中态。由 WorkspaceViewModel 在 SelectedTool 变化时维护。</summary>
    [ObservableProperty]
    private bool _isSelected;
}
