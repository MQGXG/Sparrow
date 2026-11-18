using System.Collections.ObjectModel;

namespace Sparrow.App.ViewModels;

/// <summary>剪贴板列表的时间分组（收藏 / 今天 / 三日内 / 一周内 / 更早）。</summary>
public sealed class ClipboardGroupViewModel
{
    public ClipboardGroupViewModel(string title, IEnumerable<ClipboardEntryViewModel> items)
    {
        Title = title;
        Items = new ObservableCollection<ClipboardEntryViewModel>(items);
    }

    public string Title { get; }

    public ObservableCollection<ClipboardEntryViewModel> Items { get; }
}
