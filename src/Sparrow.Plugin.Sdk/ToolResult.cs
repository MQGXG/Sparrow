using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 工具执行结果。
/// 用 <see cref="Payload"/> 表达多态，而不是四个可空字段——
/// 后者会让「发送到其他工具」退化成 null 检查，且无法表达「空字符串是合法结果」。
/// </summary>
public sealed record ToolResult
{
    public required bool Success { get; init; }

    /// <summary>成功时非空。失败时为 null。</summary>
    public IToolPayload? Payload { get; init; }

    /// <summary>失败时非空。成功时为 null。</summary>
    public ToolError? Error { get; init; }

    /// <summary>结果卡片底部可用的动作。宿主可再按 Payload 类型过滤。</summary>
    public IReadOnlyList<ToolAction> Actions { get; init; } = DefaultActions;

    /// <summary>执行耗时，由宿主填入（插件可以不填）。</summary>
    public TimeSpan Duration { get; init; }

    private static readonly IReadOnlyList<ToolAction> DefaultActions = new[]
    {
        ToolAction.Copy,
        ToolAction.Save,
        ToolAction.Pin
    };

    public static ToolResult Ok(IToolPayload payload) =>
        new() { Success = true, Payload = payload };

    public static ToolResult InvalidInput(string message, string? detail = null) =>
        new()
        {
            Success = false,
            Error = new ToolError(ToolErrorKind.InvalidInput, message, detail)
        };

    public static ToolResult Failed(string message, string? detail = null) =>
        new()
        {
            Success = false,
            Error = new ToolError(ToolErrorKind.Execution, message, detail)
        };

    public static ToolResult Timeout(string message = "执行超时") =>
        new()
        {
            Success = false,
            Error = new ToolError(ToolErrorKind.Timeout, message)
        };

    public static ToolResult Unsupported(string message) =>
        new()
        {
            Success = false,
            Error = new ToolError(ToolErrorKind.PlatformUnsupported, message)
        };

    public static ToolResult Cancelled() =>
        new()
        {
            Success = false,
            Error = new ToolError(ToolErrorKind.Cancelled, "已取消")
        };
}

/// <summary>
/// 失败详情。<see cref="Detail"/> 只进日志，不进界面（可能是堆栈）。
/// </summary>
public sealed record ToolError(ToolErrorKind Kind, string Message, string? Detail = null);

/// <summary>
/// 结果卡片底部的统一动作。
/// </summary>
public sealed record ToolAction(ToolActionKind Kind, string Label, bool IsEnabled = true)
{
    public static ToolAction Copy { get; } = new(ToolActionKind.Copy, "复制");

    public static ToolAction Save { get; } = new(ToolActionKind.Save, "保存");

    public static ToolAction Pin { get; } = new(ToolActionKind.Pin, "固定");

    public static ToolAction SendTo { get; } = new(ToolActionKind.SendTo, "发送到…");

    public static ToolAction OpenLocation { get; } = new(ToolActionKind.OpenLocation, "打开所在位置");
}
