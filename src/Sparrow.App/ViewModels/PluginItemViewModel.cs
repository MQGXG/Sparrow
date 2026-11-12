using CommunityToolkit.Mvvm.ComponentModel;

namespace Sparrow.App.ViewModels;

/// <summary>设置页里的一行插件。</summary>
public partial class PluginItemViewModel : ObservableObject
{
    public PluginItemViewModel(
        string id,
        string name,
        string version,
        int toolCount,
        bool isEnabled)
    {
        Id = id;
        Name = name;
        Version = version;
        ToolCount = toolCount;
        _isEnabled = isEnabled;
    }

    public string Id { get; }

    public string Name { get; }

    public string Version { get; }

    public int ToolCount { get; }

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>用户切换开关后通知设置页保存。</summary>
    public Action? Changed { get; init; }

    partial void OnIsEnabledChanged(bool value) => Changed?.Invoke();
}
