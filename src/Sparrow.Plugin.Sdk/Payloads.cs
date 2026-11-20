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
/// OCR 的一行文本及其在图片中的包围盒（图片坐标，物理像素）。
/// 宿主用它做选区内的行框叠加与点行复制。
/// </summary>
public sealed record OcrTextLine(
    string Text,
    double X,
    double Y,
    double Width,
    double Height);

/// <summary>一段结果的状态。</summary>
public enum TextSectionStatus
{
    /// <summary>识别成功。</summary>
    Ok,

    /// <summary>识别成功但没拿到文字。</summary>
    Empty,

    /// <summary>识别失败（解码错误 / worker 异常）。</summary>
    Failed
}

/// <summary>
/// 结构化结果里的一段（多图识别时每张图一段）。
///
/// 宿主按段渲染：只有一段时当纯文本，多段时渲染成可展开的列表。
/// <see cref="Lines"/> 是可选的行框，宿主拿它在原图上叠识别框。
/// </summary>
public sealed record TextSection
{
    /// <summary>标题，通常是「1. 文件名（1200×800）」。</summary>
    public required string Title { get; init; }

    /// <summary>正文；失败时为空。</summary>
    public string Text { get; init; } = string.Empty;

    public TextSectionStatus Status { get; init; } = TextSectionStatus.Ok;

    /// <summary>这段文字在源图片里的行框；没有就为 null。</summary>
    public IReadOnlyList<OcrTextLine>? Lines { get; init; }
}

/// <summary>
/// OCR 结果：完整文本 + 行框。
///
/// 只关心文本的调用方读 <see cref="Text"/> 即可；
/// 需要「就地识别」的宿主再读 <see cref="Lines"/> 画框；
/// <see cref="Sections"/> 是多图识别的结构化版本（每张图一段，带状态和行框）。
/// 第三方 OCR 插件仍可返回 <see cref="TextPayload"/>，宿主会回退到面板展示。
/// </summary>
public sealed record OcrResultPayload(
    string Text,
    IReadOnlyList<OcrTextLine> Lines,
    IReadOnlyList<TextSection>? Sections = null) : IToolPayload
{
    public static OcrResultPayload Empty { get; } = new(string.Empty, Array.Empty<OcrTextLine>());
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
