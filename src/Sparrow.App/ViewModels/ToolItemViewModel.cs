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
    public ToolItemViewModel(ToolEntry entry, string? highlightTerm = null)
    {
        Entry = entry;
        HighlightTerm = highlightTerm;
    }

    public ToolEntry Entry { get; }

    public ITool Tool => Entry.Tool;

    /// <summary>当前环境是否满足工具的 Requires；不满足时 UI 灰显并禁止执行。</summary>
    public bool IsAvailable => Entry.IsAvailable;

    public bool IsUnavailable => !IsAvailable;

    /// <summary>不可用的原因；可用时为 null。</summary>
    public string? UnavailableReason => Entry.UnavailableReason;

    /// <summary>不可用卡片降低不透明度，但仍可点开查看原因。</summary>
    public double CardOpacity => IsAvailable ? 1.0 : 0.55;

    /// <summary>面板搜索词，用于列表里的命中高亮；工作区为 null。</summary>
    public string? HighlightTerm { get; }

    public string Name => Entry.Descriptor.Name;

    public string Description => Entry.Descriptor.Description;

    public string CategoryLabel => Entry.Descriptor.Category;

    /// <summary>界面显示的分类名；未知分类原样显示。</summary>
    public string CategoryDisplay => ToolCategories.Display(CategoryLabel);

    /// <summary>工作区左栏的选中态。由 WorkspaceViewModel 在 SelectedTool 变化时维护。</summary>
    [ObservableProperty]
    private bool _isSelected;
}
