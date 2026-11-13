using System.Collections.ObjectModel;
using Sparrow.App.Services;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 工作区左栏的一个分类。分类来自 <c>ToolDescriptor.Category</c>，宿主不硬编码任何工具名。
/// </summary>
public sealed class ToolGroupViewModel
{
    public ToolGroupViewModel(string category, IEnumerable<ToolItemViewModel> tools)
    {
        Category = category;
        Tools = new ObservableCollection<ToolItemViewModel>(tools);
    }

    public string Category { get; }

    public string DisplayName => ToolCategories.Display(Category);

    public ObservableCollection<ToolItemViewModel> Tools { get; }
}
