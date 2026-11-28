using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Window.Pin;

/// <summary>
/// 窗口置顶：宿主进入全屏拾取模式，用户点一下目标窗口即切换置顶，
/// 置顶后标题栏旁出现图钉，点图钉取消（DeskPins 式）。
///
/// 插件只负责「切换 topmost」这一件事；拾取遮罩与图钉窗口由宿主的
/// <c>IWindowOverlay</c> 提供（见 plugin-model.md 宿主能力边界）。
/// </summary>
public sealed class WindowPinTool : ITool
{
    private readonly IWindowPinApi _api;

    /// <summary>宿主按无参构造创建工具；系统实现只在 Windows 上使用。</summary>
    public WindowPinTool() : this(new Win32WindowPinApi())
    {
    }

    public WindowPinTool(IWindowPinApi api) => _api = api;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.window.pin",
        Name = "窗口置顶",
        Category = "window",
        Description = "点一下窗口把它钉在最前面；标题栏图钉可取消",
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

        var overlay = context.Host?.Windows;
        if (overlay is null)
            return ToolResult.Unsupported("当前宿主没有提供窗口拾取能力");

        nint handle;
        try
        {
            handle = await overlay.PickWindowAsync(
                "点击窗口置顶 · 右键或 Esc 取消",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Cancelled();
        }

        // 0 = 用户取消（右键 / Esc）
        if (handle == 0)
            return ToolResult.Cancelled();

        if (!_api.TryGetTitle(handle, out var title))
            return ToolResult.InvalidInput("无法识别这个窗口，请对准有标题的窗口");

        if (_api.IsMaximized(handle))
            return ToolResult.InvalidInput("窗口已经最大化，请先还原再置顶");

        var wasTopMost = _api.IsTopMost(handle);
        if (!_api.SetTopMost(handle, !wasTopMost))
            return ToolResult.Failed($"设置窗口置顶失败：{title}");

        // 回读：系统托管窗口 / 权限隔离（UIPI）会静默拒绝 z 序变更。
        // 若照常显示图钉，用户只会看到「一个图钉浮在最前面」，目标窗口其实没钉住。
        if (_api.IsTopMost(handle) != !wasTopMost)
        {
            _api.SetTopMost(handle, wasTopMost);
            return ToolResult.Failed($"窗口拒绝置顶（系统窗口或权限限制）：{title}");
        }

        if (wasTopMost)
        {
            overlay.HidePinIndicator(handle);
            return ToolResult.Ok(new TextPayload($"已取消置顶：{title}"));
        }

        // 图钉点击 = 取消置顶；回调在宿主 UI 线程上执行
        overlay.ShowPinIndicator(handle, () =>
        {
            _api.SetTopMost(handle, false);
            overlay.HidePinIndicator(handle);
        });

        return ToolResult.Ok(new TextPayload($"已置顶：{title}"));
    }
}
