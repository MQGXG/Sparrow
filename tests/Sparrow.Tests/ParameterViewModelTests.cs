using Shouldly;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 参数选项的「值 / 显示名」分离。
/// 值必须保持插件约定的代码（OCR 用 ch/en/ja），显示名只影响下拉里看到什么。
/// </summary>
public sealed class ParameterViewModelTests
{
    private static ToolParameter Language() => new()
    {
        Key = "language",
        Label = "语言",
        Kind = ParameterKind.Choice,
        Choices = ["ch", "en", "xx"],
        ChoiceLabels = new Dictionary<string, string>
        {
            ["ch"] = "中文（ch）",
            ["en"] = "英语（en）"
        },
        Default = "ch"
    };

    [Fact]
    public void 有标签的选项显示中文名_没配的原样显示值()
    {
        var viewModel = new ParameterViewModel(Language());

        viewModel.Choices.Count.ShouldBe(3);
        viewModel.Choices[0].Value.ShouldBe("ch");
        viewModel.Choices[0].Label.ShouldBe("中文（ch）");
        viewModel.Choices[1].Label.ShouldBe("英语（en）");
        viewModel.Choices[2].Label.ShouldBe("xx", "没配标签的项不能变成空白");
    }

    [Fact]
    public void 默认值同时选中对应选项()
    {
        var viewModel = new ParameterViewModel(Language());

        viewModel.Value.ShouldBe("ch");
        viewModel.SelectedChoice.ShouldNotBeNull();
        viewModel.SelectedChoice!.Value.ShouldBe("ch");
    }

    [Fact]
    public void 用户选择后值同步回代码()
    {
        var viewModel = new ParameterViewModel(Language());

        viewModel.SelectedChoice = viewModel.Choices[1];

        viewModel.Value.ShouldBe("en", "工具逻辑读的是 Value，必须是代码而不是标签");
    }

    [Fact]
    public void 外部改值后选中项同步()
    {
        var viewModel = new ParameterViewModel(Language());

        // 恢复上次参数时走的是 Value 这条路径
        viewModel.Value = "en";

        viewModel.SelectedChoice.ShouldNotBeNull();
        viewModel.SelectedChoice!.Value.ShouldBe("en");
    }

    [Fact]
    public void 没有标签字典时显示值本身()
    {
        var parameter = new ToolParameter
        {
            Key = "layout",
            Label = "整理模式",
            Kind = ParameterKind.Choice,
            Choices = ["按行", "原文"],
            Default = "按行"
        };

        var viewModel = new ParameterViewModel(parameter);

        viewModel.Choices[0].Label.ShouldBe("按行");
        viewModel.Choices[1].Label.ShouldBe("原文");
    }

    [Fact]
    public void 多行参数_标记为多行并保留提示()
    {
        var parameter = new ToolParameter
        {
            Key = "glossary",
            Label = "术语表",
            Kind = ParameterKind.Multiline,
            Hint = "每行 原文=译文"
        };

        var viewModel = new ParameterViewModel(parameter);

        viewModel.IsText.ShouldBeTrue();
        viewModel.IsMultiline.ShouldBeTrue();
        viewModel.Hint.ShouldBe("每行 原文=译文");
        viewModel.Value.ShouldBeEmpty();
    }

    [Fact]
    public void 普通文本参数_不是多行()
    {
        var parameter = new ToolParameter
        {
            Key = "url",
            Label = "地址",
            Kind = ParameterKind.Text
        };

        var viewModel = new ParameterViewModel(parameter);

        viewModel.IsText.ShouldBeTrue();
        viewModel.IsMultiline.ShouldBeFalse();
    }
}
