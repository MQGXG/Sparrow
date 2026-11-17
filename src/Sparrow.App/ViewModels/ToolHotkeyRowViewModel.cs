using CommunityToolkit.Mvvm.ComponentModel;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 设置页「工具热键」的一行：工具名 + 可编辑的热键文本（空 = 不绑定）。
/// 工具 ID 只用于落盘，界面不展示。
/// </summary>
public sealed partial class ToolHotkeyRowViewModel : ObservableObject
{
    public ToolHotkeyRowViewModel(string toolId, string name, string hotkeyText)
    {
        ToolId = toolId;
        Name = name;
        _hotkeyText = hotkeyText;
    }

    public string ToolId { get; }

    public string Name { get; }

    [ObservableProperty]
    private string _hotkeyText = string.Empty;
}
