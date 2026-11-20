using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>一个可选项：<see cref="Value"/> 是工具逻辑用的代码，<see cref="Label"/> 是给用户看的名字。</summary>
public sealed record ParameterChoice(string Value, string Label)
{
    /// <summary>列表里没配标签时，直接显示值本身。</summary>
    public override string ToString() => Label;
}

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

        // 选项做成「值 + 标签」：像 OCR 的 ch / en / ja 这种代码，
        // 直接显示给用户毫无意义，插件可以通过 ChoiceLabels 给出中文名
        Choices = parameter.Choices
            .Select(value => new ParameterChoice(
                value,
                parameter.ChoiceLabels is { } labels && labels.TryGetValue(value, out var label)
                    ? label
                    : value))
            .ToArray();

        _value = parameter.Default ?? GetFallback(parameter);
        _selectedChoice = Choices.FirstOrDefault(choice => choice.Value == _value);
    }

    public string Key { get; }

    public string Label { get; }

    public ParameterKind Kind { get; }

    /// <summary>可选项（带显示名）；XAML 的下拉绑这个。</summary>
    public IReadOnlyList<ParameterChoice> Choices { get; }

    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>下拉选中的那一个；与 <see cref="Value"/> 双向同步。</summary>
    [ObservableProperty]
    private ParameterChoice? _selectedChoice;

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

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(BoolValue));

        // 外部把 Value 改掉时（比如恢复上次参数），选中项也要跟着换，
        // 否则下拉显示的会是旧选项
        var match = Choices.FirstOrDefault(choice => choice.Value == value);
        if (!ReferenceEquals(match, SelectedChoice))
            SelectedChoice = match;
    }

    partial void OnSelectedChoiceChanged(ParameterChoice? value)
    {
        // 用户在下拉里选了新选项：把值同步回去，工具逻辑读的仍是 Value
        if (value is not null && Value != value.Value)
            Value = value.Value;
    }

    private static string GetFallback(ToolParameter parameter) => parameter.Kind switch
    {
        ParameterKind.Toggle => "false",
        ParameterKind.Choice when parameter.Choices.Length > 0 => parameter.Choices[0],
        _ => string.Empty
    };
}
