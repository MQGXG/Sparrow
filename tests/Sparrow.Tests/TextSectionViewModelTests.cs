using Shouldly;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>多图识别的分段结果：失败项要有可读说明，长文本要有摘要。</summary>
public sealed class TextSectionViewModelTests
{
    [Fact]
    public void 失败段给出可读说明而不是空白()
    {
        var viewModel = new TextSectionViewModel(new TextSection
        {
            Title = "1. a.png",
            Status = TextSectionStatus.Failed
        });

        viewModel.IsFailed.ShouldBeTrue();
        viewModel.Body.ShouldContain("识别失败");
        viewModel.Preview.ShouldBeEmpty("失败项没有正文可摘要");
    }

    [Fact]
    public void 长文本预览截断到八十字符()
    {
        var text = new string('a', 200);

        var viewModel = new TextSectionViewModel(new TextSection
        {
            Title = "t",
            Text = text
        });

        viewModel.Preview.Length.ShouldBe(81, "80 个字符 + 一个省略号");
        viewModel.Preview.ShouldEndWith("…");
        viewModel.Text.ShouldBe(text, "正文本身不能被截断");
    }

    [Fact]
    public void 预览把换行压成空格()
    {
        var viewModel = new TextSectionViewModel(new TextSection
        {
            Title = "t",
            Text = "第一行\n第二行\r\n第三行"
        });

        viewModel.Preview.ShouldBe("第一行 第二行 第三行");
    }

    [Fact]
    public void 有源图时可以展开看原图和框()
    {
        var input = new ImageInput(new byte[] { 1, 2, 3 }, ImageFormat.Png)
        {
            Name = "a.png",
            Width = 1200,
            Height = 800
        };

        var viewModel = new TextSectionViewModel(
            new TextSection
            {
                Title = "1. a.png",
                Text = "hello",
                Lines = [new OcrTextLine("hello", 0, 0, 10, 10)]
            },
            new InputImageViewModel(input, sourcePath: null));

        viewModel.HasImage.ShouldBeTrue("宿主按 SourceIndex 找回了原图");
        viewModel.ImageWidth.ShouldBe(1200);
        viewModel.ImageHeight.ShouldBe(800);
        viewModel.HasLines.ShouldBeTrue();

        viewModel.IsExpanded.ShouldBeFalse("默认折叠，只显示一行摘要");
        viewModel.ShowBoxes.ShouldBeTrue("默认显示识别框");

        viewModel.IsExpanded = true;
        viewModel.ShowBoxes = false;

        viewModel.IsExpanded.ShouldBeTrue();
        viewModel.ShowBoxes.ShouldBeFalse();
    }

    [Fact]
    public void 没有源图时只显示文字()
    {
        var viewModel = new TextSectionViewModel(new TextSection
        {
            Title = "1. a.png",
            Text = "hello"
        });

        viewModel.HasImage.ShouldBeFalse("找不到原图就退化成纯文字，不画空画布");
        viewModel.HasLines.ShouldBeFalse();
    }

    [Fact]
    public void 行框随分段一起带过来()
    {
        var lines = new[] { new OcrTextLine("hello", 10, 20, 80, 18) };

        var viewModel = new TextSectionViewModel(new TextSection
        {
            Title = "t",
            Text = "hello",
            Lines = lines
        });

        viewModel.Lines.ShouldNotBeNull();
        viewModel.Lines!.Count.ShouldBe(1);
        viewModel.Lines[0].Text.ShouldBe("hello");
    }
}
