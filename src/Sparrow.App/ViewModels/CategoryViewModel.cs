using CommunityToolkit.Mvvm.ComponentModel;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 侧栏分类导航项。「全部」是虚拟分类，Name 固定为「全部」，SelectedCategory 用 null 表示它。
/// </summary>
public sealed partial class CategoryViewModel : ObservableObject
{
    public CategoryViewModel(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; }

    public int Count { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 工具墙「最近使用」行的一个 chip：工具 + 最后一次执行时间。
/// </summary>
public sealed record RecentToolViewModel(ToolItemViewModel Tool, string TimeLabel);
