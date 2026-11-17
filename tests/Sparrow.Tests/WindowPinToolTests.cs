using Shouldly;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Window.Pin;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 窗口置顶插件的执行流程：不依赖真实鼠标和窗口，用假 API 复现
/// 「等松开 → 点目标 → 切换置顶」以及取消/超时/非法目标。
/// </summary>
public sealed class WindowPinToolTests
{
    private sealed class FakePinApi : IWindowPinApi
    {
        private readonly Queue<bool> _buttonStates = new();

        public bool Cancel { get; set; }
        public bool HasWindow { get; set; } = true;
        public nint Handle { get; set; } = 42;
        public string? Title { get; set; } = "记事本";
        public bool OwnWindow { get; set; }
        public bool TopMost { get; set; }
        public bool SetSucceeds { get; set; } = true;
        public List<(nint Handle, bool TopMost)> SetCalls { get; } = [];

        /// <summary>模拟：执行按钮已松开 → 用户在目标窗口点一下 → 松开。</summary>
        public FakePinApi PressLeftButton()
        {
            _buttonStates.Enqueue(false);
            _buttonStates.Enqueue(true);
            _buttonStates.Enqueue(false);
            return this;
        }

        public bool IsCancelPressed() => Cancel;

        public bool IsLeftButtonDown() =>
            _buttonStates.Count > 0 && _buttonStates.Dequeue();

        public bool TryGetPointedWindow(out nint handle)
        {
            handle = Handle;
            return HasWindow;
        }

        public bool TryGetTitle(nint handle, out string title)
        {
            title = Title ?? string.Empty;
            return !string.IsNullOrWhiteSpace(Title);
        }

        public bool IsOwnProcessWindow(nint handle) => OwnWindow;

        public bool IsTopMost(nint handle) => TopMost;

        public bool SetTopMost(nint handle, bool topMost)
        {
            SetCalls.Add((handle, topMost));
            return SetSucceeds;
        }
    }

    private static Task<ToolResult> ExecuteAsync(FakePinApi api, TimeSpan? timeout = null) =>
        new WindowPinTool(api, timeout).ExecuteAsync(
            new ToolContext { Input = EmptyInput.Instance },
            CancellationToken.None);

    [Fact]
    public void 描述符_符合插件命名与输入形态()
    {
        var tool = new WindowPinTool(new FakePinApi());

        tool.Descriptor.Id.ShouldBe("sparrow.window.pin");
        tool.Descriptor.Category.ShouldBe("window");
        tool.Descriptor.Accepts.ShouldBe(InputShape.None);
        tool.Descriptor.Keywords.Length.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task 点击普通窗口_置顶并返回标题()
    {
        var api = new FakePinApi { TopMost = false }.PressLeftButton();

        var result = await ExecuteAsync(api);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>();
        ((TextPayload)result.Payload!).Text.ShouldBe("已置顶：记事本");
        api.SetCalls.ShouldHaveSingleItem().ShouldBe(((nint)42, true));
    }

    [Fact]
    public async Task 点击已置顶窗口_取消置顶()
    {
        var api = new FakePinApi { TopMost = true }.PressLeftButton();

        var result = await ExecuteAsync(api);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>();
        ((TextPayload)result.Payload!).Text.ShouldBe("已取消置顶：记事本");
        api.SetCalls.ShouldHaveSingleItem().ShouldBe(((nint)42, false));
    }

    [Fact]
    public async Task 点到桌面或系统窗口_提示换一个()
    {
        var api = new FakePinApi { HasWindow = false }.PressLeftButton();

        var result = await ExecuteAsync(api);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
        result.Error.Message.ShouldContain("不支持置顶");
    }

    [Fact]
    public async Task 点到Sparrow自己的窗口_拒绝()
    {
        var api = new FakePinApi { OwnWindow = true }.PressLeftButton();

        var result = await ExecuteAsync(api);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
        result.Error.Message.ShouldContain("Sparrow");
    }

    [Fact]
    public async Task 窗口没有标题_拒绝()
    {
        var api = new FakePinApi { Title = null }.PressLeftButton();

        var result = await ExecuteAsync(api);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
        result.Error.Message.ShouldContain("有标题");
    }

    [Fact]
    public async Task 设置失败_返回执行错误()
    {
        var api = new FakePinApi { SetSucceeds = false }.PressLeftButton();

        var result = await ExecuteAsync(api);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
        result.Error.Message.ShouldContain("失败");
    }

    [Fact]
    public async Task 按Esc_取消()
    {
        var api = new FakePinApi { Cancel = true };

        var result = await ExecuteAsync(api);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Cancelled);
    }

    [Fact]
    public async Task 超时_返回超时错误()
    {
        var api = new FakePinApi();

        var result = await ExecuteAsync(api, TimeSpan.Zero);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Timeout);
    }
}
