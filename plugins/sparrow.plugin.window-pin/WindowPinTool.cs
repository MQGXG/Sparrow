using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Window.Pin;

/// <summary>
/// 窗口置顶：执行后进入「拾取窗口」状态，用户在目标窗口上点一下即切换置顶。
///
/// 为什么不做成宿主能力：这是「一次调用完成一件事」的工具，
/// 插件自己 P/Invoke 系统 API 就能完成，不需要宿主 UI（见 plugin-model.md 宿主能力边界）。
/// </summary>
public sealed class WindowPinTool : ITool
{
    private const int PollMilliseconds = 30;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IWindowPinApi _api;
    private readonly TimeSpan _timeout;

    /// <summary>宿主按无参构造创建工具；系统实现只在 Windows 上使用。</summary>
    public WindowPinTool() : this(new Win32WindowPinApi())
    {
    }

    public WindowPinTool(IWindowPinApi api, TimeSpan? timeout = null)
    {
        _api = api;
        _timeout = timeout ?? DefaultTimeout;
    }

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.window.pin",
        Name = "窗口置顶",
        Category = "window",
        Description = "点一下任意窗口，把它钉在最前面；再点一次取消",
        Keywords = ["置顶", "窗口", "钉住", "deskpins", "topmost", "pin"],
        Accepts = InputShape.None,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.None,
        Order = 5
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return ToolResult.Unsupported("窗口置顶目前只支持 Windows");

        try
        {
            return await ExecuteCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Cancelled();
        }
    }

    private async Task<ToolResult> ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _timeout;

        // 1. 先等「执行」那一下的左键抬起：否则会把执行按钮的点击当成选择窗口
        while (_api.IsLeftButtonDown())
        {
            var early = await TickAsync(cancellationToken, deadline);
            if (early is not null)
                return early;
        }

        // 2. 等用户在目标窗口上点一下
        while (true)
        {
            var early = await TickAsync(cancellationToken, deadline);
            if (early is not null)
                return early;

            if (!_api.IsLeftButtonDown())
                continue;

            // 松开后再取窗口：按下瞬间窗口可能还没完成激活
            while (_api.IsLeftButtonDown())
            {
                var released = await TickAsync(cancellationToken, deadline);
                if (released is not null)
                    return released;
            }

            break;
        }

        if (!_api.TryGetPointedWindow(out var handle))
            return ToolResult.InvalidInput("这个窗口不支持置顶（桌面或系统窗口），请换一个窗口");

        if (_api.IsOwnProcessWindow(handle))
            return ToolResult.InvalidInput("不能置顶 Sparrow 自己的窗口，请选择其他窗口");

        if (!_api.TryGetTitle(handle, out var title))
            return ToolResult.InvalidInput("无法识别这个窗口，请对准有标题的窗口");

        var wasTopMost = _api.IsTopMost(handle);
        if (!_api.SetTopMost(handle, !wasTopMost))
            return ToolResult.Failed($"设置窗口置顶失败：{title}");

        return ToolResult.Ok(new TextPayload(wasTopMost
            ? $"已取消置顶：{title}"
            : $"已置顶：{title}"));
    }

    /// <summary>一次轮询：先看取消/超时，再让出 UI 线程。返回非 null 表示应结束。</summary>
    private async Task<ToolResult?> TickAsync(CancellationToken cancellationToken, DateTime deadline)
    {
        if (_api.IsCancelPressed())
            return ToolResult.Cancelled();

        if (DateTime.UtcNow >= deadline)
            return ToolResult.Timeout($"等待选择窗口超时（{_timeout.TotalSeconds:0} 秒）");

        await Task.Delay(PollMilliseconds, cancellationToken);
        return null;
    }
}
