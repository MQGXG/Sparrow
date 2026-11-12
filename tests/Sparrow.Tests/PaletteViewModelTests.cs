using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Json.Format;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 壳层链路的自动化验证：搜索 → 选中 → 执行 → 结果 → 复制。
/// 这层不启动 Avalonia，只测 ViewModel 与 Registry 的行为，
/// 对话框和托盘仍然需要人工确认。
/// </summary>
public sealed class PaletteViewModelTests
{
    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public string? GetText() => Text;

        public bool SetText(string text)
        {
            Text = text;
            return true;
        }
    }

    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static (PaletteViewModel ViewModel, FakeClipboard Clipboard) Create(string? clipboardText)
    {
        var clipboard = new FakeClipboard { Text = clipboardText };
        // 用工厂模拟宿主：ViewModel 首次搜索时才创建注册表
        return (new PaletteViewModel(
            () => new ToolRegistry([new JsonFormatTool()]),
            clipboard,
            new NullLog()), clipboard);
    }

    [Fact]
    public void 搜索json_命中格式化工具()
    {
        var (viewModel, _) = Create(null);

        viewModel.Query = "json";

        viewModel.Items.Count.ShouldBe(1);
        viewModel.Selected!.Tool.ShouldBeOfType<JsonFormatTool>();
        viewModel.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void 搜索名称_优先级高于描述匹配()
    {
        var (viewModel, _) = Create(null);

        viewModel.Query = "格式化";

        viewModel.Selected!.Tool.ShouldBeOfType<JsonFormatTool>();
    }

    [Fact]
    public void 搜索无匹配_显示空状态()
    {
        var (viewModel, _) = Create(null);

        viewModel.Query = "这个工具不存在";

        viewModel.Items.ShouldBeEmpty();
        viewModel.Selected.ShouldBeNull();
        viewModel.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task 执行_从剪贴板取输入并返回格式化结果()
    {
        var (viewModel, _) = Create("""{"a":1}""");
        viewModel.Query = "json";

        await viewModel.ExecuteAsync();

        viewModel.HasResult.ShouldBeTrue();
        viewModel.HasError.ShouldBeFalse();
        viewModel.ResultText!.ShouldContain("\n  \"a\": 1");
    }

    [Fact]
    public async Task 执行_剪贴板为空_显示用户可读错误()
    {
        var (viewModel, _) = Create(null);
        viewModel.Query = "json";

        await viewModel.ExecuteAsync();

        viewModel.HasError.ShouldBeTrue();
        viewModel.HasResult.ShouldBeFalse();
        viewModel.ErrorText.ShouldBe("输入为空");
    }

    [Fact]
    public async Task 执行_非法JSON_错误信息带行列号()
    {
        var (viewModel, _) = Create("""{"a": }""");
        viewModel.Query = "json";

        await viewModel.ExecuteAsync();

        viewModel.HasError.ShouldBeTrue();
        viewModel.ErrorText!.ShouldContain("第 1 行");
    }

    [Fact]
    public async Task 复制结果_写入剪贴板并更新标题()
    {
        var (viewModel, clipboard) = Create("""{"a":1}""");
        viewModel.Query = "json";
        await viewModel.ExecuteAsync();

        viewModel.CopyResult();

        clipboard.Text.ShouldBe(viewModel.ResultText);
        viewModel.ResultTitle.ShouldBe("已复制到剪贴板");
    }

    [Fact]
    public async Task 重新打开面板_清掉上一次的结果但保留查询词()
    {
        var (viewModel, _) = Create("""{"a":1}""");
        viewModel.Query = "json";
        await viewModel.ExecuteAsync();

        viewModel.Reset();

        viewModel.Query.ShouldBe("json");
        viewModel.HasResult.ShouldBeFalse();
        viewModel.HasError.ShouldBeFalse();
        viewModel.ResultText.ShouldBeNull();
    }
}
