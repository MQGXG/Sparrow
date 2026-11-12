using Sparrow.Plugin.Sdk.Inputs;

namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 工具执行时能看到的全部输入。
/// 工具不关心数据来自剪贴板还是截图，只看 <see cref="Input"/> 的具体类型。
/// </summary>
public sealed record ToolContext
{
    /// <summary>输入内容。必填。</summary>
    public required IToolInput Input { get; init; }

    /// <summary>来源标签，仅供展示与日志，**不得用于分支逻辑**。</summary>
    public ToolSource Source { get; init; } = ToolSource.Manual;

    /// <summary>参数值。统一为 string，转换失败由工具返回 InvalidInput 而不是抛异常。</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = EmptyParameters;

    /// <summary>一次接力链共用的追踪 ID，用于历史聚合与日志关联。</summary>
    public string? TraceId { get; init; }

    /// <summary>接力深度。宿主要在 &gt;= 3 时禁止「发送到其他工具」。</summary>
    public int Depth { get; init; }

    /// <summary>进度上报，可为 null（工具必须判空）。</summary>
    public IProgress<ToolProgress>? Progress { get; init; }

    /// <summary>
    /// 宿主注入的能力（日志、设置、受限网络、插件目录）。
    ///
    /// 可能为 null：测试宿主或早期调用方没有注入时，插件必须判空。
    /// 放在这里而不是 <c>ITool.Initialize</c>，是为了保持
    /// 「ITool 只有两个成员、实现必须无状态」这条约束。
    /// </summary>
    public IToolHost? Host { get; init; }

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(0);

    /// <summary>读取参数，不存在返回 null。</summary>
    public string? GetParameter(string key) =>
        Parameters.TryGetValue(key, out var value) ? value : null;

    /// <summary>读取参数，不存在或为空时返回 fallback。</summary>
    public string GetParameter(string key, string fallback) =>
        Parameters.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

    /// <summary>读取整数参数，解析失败返回 fallback。</summary>
    public int GetParameter(string key, int fallback) =>
        int.TryParse(GetParameter(key), out var value) ? value : fallback;

    /// <summary>读取布尔参数，解析失败返回 fallback。</summary>
    public bool GetParameter(string key, bool fallback) =>
        bool.TryParse(GetParameter(key), out var value) ? value : fallback;
}

/// <summary>
/// 进度上报。Percent 为 null 表示不确定进度（显示为无限循环）。
/// </summary>
public sealed record ToolProgress(double? Percent, string? Message = null)
{
    public static ToolProgress Indeterminate(string? message = null) => new(null, message);
}
