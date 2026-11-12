using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 一个工具参数的编辑控件。
///
/// 这是宿主根据 <see cref="ToolParameter"/> **自动生成**的，
/// 插件不提供任何 UI 代码——参数声明就是插件的全部界面贡献。
/// </summary>
public partial class ParameterViewModel : ObservableObject
{
    public ParameterViewModel(ToolParameter parameter)
    {
        Key = parameter.Key;
        Label = parameter.Label;
        Kind = parameter.Kind;
        Choices = parameter.Choices;
        _value = parameter.Default ?? GetFallback(parameter);
    }

    public string Key { get; }

    public string Label { get; }

    public ParameterKind Kind { get; }

    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    private string _value = string.Empty;

    public bool IsChoice => Kind == ParameterKind.Choice;

    public bool IsToggle => Kind == ParameterKind.Toggle;

    public bool IsText => Kind is ParameterKind.Text
        or ParameterKind.Number
        or ParameterKind.FilePath
        or ParameterKind.Color
        or ParameterKind.Multiline;

    /// <summary>Toggle 用的布尔视图，底层仍存字符串（与 ToolContext.Parameters 一致）。</summary>
    public bool BoolValue
    {
        get => bool.TryParse(Value, out var result) && result;
        set => Value = value ? "true" : "false";
    }

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(BoolValue));

    private static string GetFallback(ToolParameter parameter) => parameter.Kind switch
    {
        ParameterKind.Toggle => "false",
        ParameterKind.Choice when parameter.Choices.Length > 0 => parameter.Choices[0],
        _ => string.Empty
    };
}
