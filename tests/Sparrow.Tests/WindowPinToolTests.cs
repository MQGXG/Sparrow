using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Window.Pin;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 窗口置顶插件的执行流程：拾取与图钉由宿主的 IWindowOverlay 提供，
/// 这里用假实现验证「置顶 / 取消 / 点击图钉取消」的业务判断。
/// </summary>
public sealed class WindowPinToolTests
{
    private sealed class FakeOverlay : IWindowOverlay
    {
        public nint PickResult { get; set; } = 42;
        public bool PickCancelled { get; set; }
        public string? LastHint { get; private set; }
        public List<(nint Window, Action OnRemove)> Shown { get; } = [];
        public List<nint> Hidden { get; } = [];

        public Task<nint> PickWindowAsync(string hint, CancellationToken cancellationToken)
        {
            LastHint = hint;

            return PickCancelled
                ? Task.FromCanceled<nint>(new CancellationToken(canceled: true))
                : Task.FromResult(PickResult);
        }

        public void ShowPinIndicator(nint window, Action onRemove) => Shown.Add((window, onRemove));

        public void HidePinIndicator(nint window) => Hidden.Add(window);
    }

    private sealed class FakePinApi : IWindowPinApi
    {
        public string? Title { get; set; } = "记事本";
        public bool TopMost { get; set; }
        public bool SetSucceeds { get; set; } = true;
        public List<(nint Handle, bool TopMost)> SetCalls { get; } = [];

        public bool TryGetTitle(nint handle, out string title)
        {
            title = Title ?? string.Empty;
            return !string.IsNullOrWhiteSpace(Title);
        }

        public bool IsTopMost(nint handle) => TopMost;

        public bool SetTopMost(nint handle, bool topMost)
        {
            SetCalls.Add((handle, topMost));
            TopMost = topMost;
            return SetSucceeds;
        }
    }

    private sealed class FakeHost : IToolHost
    {
        public FakeHost(IWindowOverlay? windows) => Windows = windows;

        public ILogger Logger => NullLogger.Instance;

        public IToolSettings Settings { get; } = new FakeSettings();

        public IHttpClientAccess Http => throw new NotSupportedException();

        public IAiClient Ai => throw new NotSupportedException();

        public ILocalModels LocalModels => NullLocalModels.Instance;

        public string PluginDirectory => AppContext.BaseDirectory;

        public IClipboardHistory? ClipboardHistory => null;

        public IWindowOverlay? Windows { get; }

        private sealed class FakeSettings : IToolSettings
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

            public string? Get(string key) =>
                _values.TryGetValue(key, out var value) ? value : null;

            public void Set(string key, string? value)
            {
                if (value is null)
                    _values.Remove(key);
                else
                    _values[key] = value;
            }
        }
    }

    private static Task<ToolResult> ExecuteAsync(
        FakePinApi api,
        FakeOverlay? overlay = null) =>
        new WindowPinTool(api).ExecuteAsync(
            new ToolContext
            {
                Input = EmptyInput.Instance,
                Host = new FakeHost(overlay ?? new FakeOverlay())
            },
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
    public async Task 选择普通窗口_置顶并显示图钉()
    {
        var api = new FakePinApi { TopMost = false };
        var overlay = new FakeOverlay();

        var result = await ExecuteAsync(api, overlay);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>();
        ((TextPayload)result.Payload!).Text.ShouldBe("已置顶：记事本");

        api.SetCalls.ShouldHaveSingleItem().ShouldBe(((nint)42, true));
        overlay.Shown.ShouldHaveSingleItem().Window.ShouldBe((nint)42);
        overlay.LastHint.ShouldNotBeNull().ShouldContain("点击窗口置顶");
    }

    [Fact]
    public async Task 选择已置顶窗口_取消置顶并移除图钉()
    {
        var api = new FakePinApi { TopMost = true };
        var overlay = new FakeOverlay();

        var result = await ExecuteAsync(api, overlay);

        result.Success.ShouldBeTrue();
        ((TextPayload)result.Payload!).Text.ShouldBe("已取消置顶：记事本");

        api.SetCalls.ShouldHaveSingleItem().ShouldBe(((nint)42, false));
        overlay.Hidden.ShouldContain((nint)42);
        overlay.Shown.ShouldBeEmpty();
    }

    [Fact]
    public async Task 点击图钉_取消置顶并移除图钉()
    {
        var api = new FakePinApi { TopMost = false };
        var overlay = new FakeOverlay();

        var result = await ExecuteAsync(api, overlay);
        result.Success.ShouldBeTrue();

        // 模拟用户点击宿主显示的图钉
        overlay.Shown.Single().OnRemove();

        api.SetCalls.ShouldContain(((nint)42, false));
        overlay.Hidden.ShouldContain((nint)42);
    }

    [Fact]
    public async Task 取消选择_返回已取消()
    {
        var api = new FakePinApi();
        var overlay = new FakeOverlay { PickResult = 0 };

        var result = await ExecuteAsync(api, overlay);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Cancelled);
        api.SetCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task 拾取被取消_返回已取消()
    {
        var api = new FakePinApi();
        var overlay = new FakeOverlay { PickCancelled = true };

        var result = await ExecuteAsync(api, overlay);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Cancelled);
        api.SetCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task 宿主没有覆盖能力_返回不支持()
    {
        var api = new FakePinApi();

        var result = await new WindowPinTool(api).ExecuteAsync(
            new ToolContext { Input = EmptyInput.Instance, Host = new FakeHost(null) },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.PlatformUnsupported);
        result.Error.Message.ShouldContain("窗口拾取");
    }

    [Fact]
    public async Task 窗口没有标题_拒绝()
    {
        var api = new FakePinApi { Title = null };
        var overlay = new FakeOverlay();

        var result = await ExecuteAsync(api, overlay);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
        result.Error.Message.ShouldContain("有标题");
    }

    [Fact]
    public async Task 设置失败_返回执行错误()
    {
        var api = new FakePinApi { SetSucceeds = false };
        var overlay = new FakeOverlay();

        var result = await ExecuteAsync(api, overlay);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
        result.Error.Message.ShouldContain("失败");
    }
}
