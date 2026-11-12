using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Json.Format;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
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

        public byte[]? ImagePng { get; set; }

        public string? GetText() => Text;

        public byte[]? GetImagePng() => ImagePng;

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
            () => ToolRegistry.FromTools([new JsonFormatTool()]),
            clipboard,
            new NullLog(),
            new NullHistoryStore()), clipboard);
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

    /// <summary>
    /// 执行中的重复回车必须被挡住。
    /// OCR 一次要 2–3 秒，用户很自然会再按一次 Enter；
    /// 没有这道闸，会起两个 worker 进程并互相覆盖结果。
    /// </summary>
    [Fact]
    public async Task 执行中再次回车_不会重复执行()
    {
        var tool = new SlowTool();
        var viewModel = new PaletteViewModel(
            () => ToolRegistry.FromTools([tool]),
            new FakeClipboard { Text = "输入" },
            new NullLog(),
            new NullHistoryStore());

        viewModel.Query = "慢";
        viewModel.Selected.ShouldNotBeNull();

        var firstRun = viewModel.ExecuteAsync();
        await tool.Started;

        viewModel.IsExecuting.ShouldBeTrue();

        // 第二次按回车：应被 IsExecuting 挡住，立即返回
        await viewModel.ExecuteAsync();

        tool.Release();
        await firstRun;

        tool.Runs.ShouldBe(1);
        viewModel.IsExecuting.ShouldBeFalse();
    }

    /// <summary>可被测试控制何时结束的工具。</summary>
    private sealed class SlowTool : ITool
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "test.slow",
            Name = "慢工具",
            Category = "test",
            Accepts = InputShape.Text
        };

        public int Runs => _runs;

        public Task Started => _started.Task;

        public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            _started.TrySetResult();
            await _release.Task;
            return new ToolResult { Success = true, Payload = new TextPayload("done") };
        }

        public void Release() => _release.TrySetResult();
    }
}
