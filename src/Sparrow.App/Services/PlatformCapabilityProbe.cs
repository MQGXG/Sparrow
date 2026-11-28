using System.Runtime.InteropServices;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 宿主能力探测：把当前环境支持的能力位与插件声明的 <c>Requires</c> 做比较。
/// </summary>
public interface IPlatformCapabilityProbe
{
    PlatformCapability Supported { get; }

    /// <summary>
    /// 返回缺失能力的可读原因；全部满足时返回 null。
    /// </summary>
    string? DescribeMissing(PlatformCapability required);
}

/// <summary>
/// 当前宿主的真实能力。
///
/// 这里是「宿主探测、插件声明」的唯一落点：插件只写 <c>Requires</c>，
/// 不自己判断操作系统；不满足的工具由注册表标记为不可用，UI 灰显并给出原因。
/// </summary>
public sealed class HostPlatformCapabilityProbe : IPlatformCapabilityProbe
{
    private const PlatformCapability Baseline =
        PlatformCapability.Clipboard
        | PlatformCapability.ClipboardMonitor
        | PlatformCapability.Screenshot
        | PlatformCapability.Selection
        | PlatformCapability.GlobalHotkey
        | PlatformCapability.SvgRender;

    private readonly IAiClient? _ai;
    private readonly IHttpClientAccess? _http;

    public HostPlatformCapabilityProbe(IAiClient? ai = null, IHttpClientAccess? http = null)
    {
        _ai = ai;
        _http = http;
    }

    public PlatformCapability Supported
    {
        get
        {
            var supported = Baseline;

            // OCR worker 只有 win-x64 构建；其他架构明确不可用
            if (OperatingSystem.IsWindows()
                && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                supported |= PlatformCapability.Ocr;
            }

            // Network / Ai 不是操作系统能力，而是宿主当前是否真的能用
            if (_http?.IsAvailable == true)
                supported |= PlatformCapability.Network;

            if (_ai?.IsAvailable == true)
                supported |= PlatformCapability.Ai;

            return supported;
        }
    }

    public string? DescribeMissing(PlatformCapability required)
    {
        var missing = required & ~Supported;
        if (missing == PlatformCapability.None)
            return null;

        var names = new List<string>();

        if (missing.HasFlag(PlatformCapability.Clipboard)) names.Add("剪贴板");
        if (missing.HasFlag(PlatformCapability.ClipboardMonitor)) names.Add("剪贴板监听");
        if (missing.HasFlag(PlatformCapability.Screenshot)) names.Add("截图");
        if (missing.HasFlag(PlatformCapability.Selection)) names.Add("读取选区");
        if (missing.HasFlag(PlatformCapability.GlobalHotkey)) names.Add("全局热键");
        if (missing.HasFlag(PlatformCapability.Ocr)) names.Add("OCR（仅 Windows x64）");
        if (missing.HasFlag(PlatformCapability.SvgRender)) names.Add("SVG 渲染");
        if (missing.HasFlag(PlatformCapability.Network)) names.Add("网络访问（宿主未启用）");
        if (missing.HasFlag(PlatformCapability.Ai)) names.Add("AI 模型（未配置）");

        return names.Count == 0
            ? "当前环境不支持这个工具"
            : "当前环境不支持：" + string.Join("、", names);
    }
}
