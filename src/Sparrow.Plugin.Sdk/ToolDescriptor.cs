namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 工具的静态元数据。
/// 必须是可静态读取的（不依赖 InitializeAsync），否则命令面板要加载程序集才能搜索，
/// 冷启动会变慢——这是 IITool 只有一个执行方法的原因（见 docs/architecture.md 第四节）。
/// </summary>
public sealed record ToolDescriptor
{
    /// <summary>工具唯一 ID，格式 sparrow.&lt;分类&gt;.&lt;动作&gt;，进入历史库后不可更改。</summary>
    public required string Id { get; init; }

    /// <summary>面板显示名。</summary>
    public required string Name { get; init; }

    /// <summary>分类，用于工作区左栏分组。</summary>
    public required string Category { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>搜索关键词，至少 3 个，必须包含中文口语词。</summary>
    public string[] Keywords { get; init; } = Array.Empty<string>();

    public InputShape Accepts { get; init; } = InputShape.None;

    public OutputShape Produces { get; init; } = OutputShape.None;

    /// <summary>所需平台能力。不满足时工具灰显，不加载执行。</summary>
    public PlatformCapability Requires { get; init; } = PlatformCapability.None;

    /// <summary>参数声明。上限 5 个，超过说明这个工具在做两件事。</summary>
    public IReadOnlyList<ToolParameter> Parameters { get; init; } = Array.Empty<ToolParameter>();

    /// <summary>面板默认排序权重，越大越靠前。</summary>
    public int Order { get; init; }
}

/// <summary>
/// 参数声明。宿主据此自动生成控件，插件不提供任何 UI。
/// </summary>
public sealed record ToolParameter
{
    /// <summary>参数键，工具内唯一。</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    public ParameterKind Kind { get; init; } = ParameterKind.Text;

    /// <summary>默认值，统一用字符串表达（见 docs/context-model.md 第三节）。</summary>
    public string? Default { get; init; }

    /// <summary>Kind == Choice 时的可选项。</summary>
    public string[] Choices { get; init; } = Array.Empty<string>();

    public string? Hint { get; init; }

    /// <summary>根据 Key 查找参数声明，找不到返回 null。</summary>
    public static ToolParameter? Find(IReadOnlyList<ToolParameter> parameters, string key)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (string.Equals(parameters[i].Key, key, StringComparison.Ordinal))
                return parameters[i];
        }
        return null;
    }
}
