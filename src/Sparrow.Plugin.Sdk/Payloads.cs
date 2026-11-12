namespace Sparrow.Plugin.Sdk.Payloads;

/// <summary>
/// 所有结果的公共标记接口。
/// 用判别式联合替代「一堆可空字段」，让下游的「发送到其他工具」可以安全匹配。
/// </summary>
public interface IToolPayload
{
}

/// <summary>
/// 纯文本结果。
/// </summary>
public sealed record TextPayload(string Text) : IToolPayload
{
    /// <summary>超长文本被宿主截断时为 true。</summary>
    public bool IsTruncated { get; init; }

    public static TextPayload Empty { get; } = new(string.Empty);
}

/// <summary>
/// SVG 源码结果。宿主负责渲染预览与安全清洗。
/// </summary>
public sealed record SvgPayload(string Svg) : IToolPayload
{
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// 位图结果。
/// </summary>
public sealed record ImagePayload(ReadOnlyMemory<byte> Data, ImageFormat Format) : IToolPayload
{
    public int Width { get; init; }
    public int Height { get; init; }

    public int ByteSize => Data.Length;
}

/// <summary>
/// 一组文件结果。宿主负责展示与「打开所在位置」，插件不直接写盘。
/// </summary>
public sealed record FileSetPayload(IReadOnlyList<string> Paths) : IToolPayload
{
    public static FileSetPayload Empty { get; } = new(Array.Empty<string>());
}

/// <summary>
/// 表格结果。第一行是表头，其余是数据行。
/// </summary>
public sealed record TablePayload(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows) : IToolPayload
{
    public static TablePayload Empty { get; } = new(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
}
