using System.Collections.ObjectModel;
using Sparrow.App.Services;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 工作区左栏的一个分类。分类来自 <c>ToolDescriptor.Category</c>，宿主不硬编码任何工具名。
/// </summary>
public sealed class ToolGroupViewModel
{
    public ToolGroupViewModel(string category, IEnumerable<ToolEntry> entries)
    {
        Category = category;
        Tools = new ObservableCollection<ToolItemViewModel>(
            entries.Select(entry => new ToolItemViewModel(entry)));
    }

    public string Category { get; }

    public ObservableCollection<ToolItemViewModel> Tools { get; }
}
