using CommunityToolkit.Mvvm.ComponentModel;

namespace Sparrow.App.ViewModels;

/// <summary>设置页里的一行索引盘选项。</summary>
public partial class IndexDriveOptionViewModel : ObservableObject
{
    private readonly Action _changed;

    public IndexDriveOptionViewModel(
        string drive, string label, bool isSelected, Action changed)
    {
        Drive = drive;
        Label = label;
        _isSelected = isSelected;
        _changed = changed;
    }

    public string Drive { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}
