using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Services;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 工作区工具墙的一个分类分组。分类来自 <c>ToolDescriptor.Category</c>，宿主不硬编码任何工具名。
/// </summary>
public sealed partial class ToolGroupViewModel : ObservableObject
{
    public ToolGroupViewModel(string category, IEnumerable<ToolItemViewModel> tools)
    {
        Category = category;
        Tools = new ObservableCollection<ToolItemViewModel>(tools);
    }

    public string Category { get; }

    public string DisplayName => ToolCategories.Display(Category);

    public ObservableCollection<ToolItemViewModel> Tools { get; }

    /// <summary>组内卡片是否展开；由 WorkspaceViewModel 统一计算（搜索强制展开 / chip 联动 / 手动折叠）。</summary>
    [ObservableProperty]
    private bool _isExpanded = true;
}
