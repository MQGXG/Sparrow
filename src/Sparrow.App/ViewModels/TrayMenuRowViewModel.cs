namespace Sparrow.App.ViewModels;

/// <summary>设置页「托盘」里的一行菜单项。</summary>
public sealed record TrayMenuRowViewModel(int Position, string Label, bool IsSeparator)
{
    public string IndexLabel => Position.ToString("D2");
}

/// <summary>可以添加到托盘的下拉项：宿主动作或某个可无头执行的工具。</summary>
public sealed record TrayMenuOptionViewModel(string Label, string? Action, string? Tool);
