using Shouldly;
using Sparrow.App.ViewModels;
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
