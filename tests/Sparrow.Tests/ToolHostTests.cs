using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 宿主能力注入的验证。
/// 这是 OCR 的前置：进程插件必须能从 Host 拿到自己的目录，才能找到 worker exe。
/// </summary>
public sealed class ToolHostTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; } = "probe";

        public byte[]? ImagePng { get; set; }

        public string? GetText() => Text;

        public byte[]? GetImagePng() => ImagePng;

        public bool SetText(string text)
        {
            Text = text;
            return true;
        }
    }

    /// <summary>记录最后一次执行拿到的 ToolContext。</summary>
    private sealed class HostProbeTool : ITool
    {
        public ToolContext? LastContext { get; private set; }

        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "sparrow.test.host-probe",
            Name = "宿主探针",
            Category = "test",
            Accepts = InputShape.Text,
            Produces = OutputShape.Text
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
        {
            LastContext = context;
            return Task.FromResult(ToolResult.Ok(new TextPayload("ok")));
        }
    }

    [Fact]
    public void 默认网络关闭_设置读写可用且可为空()
    {
        var host = new ToolHost("sparrow.plugin.test", @"C:\plugins\test", new NullLog());

        host.PluginDirectory.ShouldBe(@"C:\plugins\test");
        host.Http.IsAvailable.ShouldBeFalse();

        host.Settings.Get("missing").ShouldBeNull();

        host.Settings.Set("key", "value");
        host.Settings.Get("key").ShouldBe("value");

        host.Settings.Set("key", null);
        host.Settings.Get("key").ShouldBeNull();
    }

    [Fact]
    public void 网络禁用时_调用SendAsync抛异常而不是静默失败()
    {
        var host = new ToolHost("sparrow.plugin.test", ".", new NullLog());

        Should.Throw<InvalidOperationException>(() =>
            host.Http.SendAsync(new HttpRequestMessage(), CancellationToken.None));
    }

    [Fact]
    public async Task 执行时_宿主能力会通过ToolContext传给工具()
    {
        var probe = new HostProbeTool();
        var viewModel = new PaletteViewModel(
            () => ToolRegistry.FromTools([probe]),
            new FakeClipboard(),
            new NullLog(),
            new NullHistoryStore());

        viewModel.Query = "宿主探针";

        await viewModel.ExecuteAsync();

        probe.LastContext.ShouldNotBeNull();
        probe.LastContext!.Host.ShouldNotBeNull();
        probe.LastContext.Host!.PluginDirectory.ShouldNotBeNullOrEmpty();
    }
}
