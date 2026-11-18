namespace Sparrow.Plugin.Sdk.Inputs;

/// <summary>
/// 所有输入的公共标记接口。
/// 工具用 <c>is TextInput { Text: var text }</c> 做模式匹配，不关心来源。
/// </summary>
public interface IToolInput
{
}

/// <summary>
/// 纯文本 / 代码输入。
/// </summary>
public sealed record TextInput(string Text) : IToolInput
{
    /// <summary>超长文本被宿主截断时为 true（阈值 256KB，见 docs/context-model.md 第五节）。</summary>
    public bool IsTruncated { get; init; }

    public static TextInput Empty { get; } = new(string.Empty);
}

/// <summary>
/// 位图输入。
/// 用 ReadOnlyMemory 而非拷贝，交给工具后由宿主负责生命周期。
/// </summary>
public sealed record ImageInput(ReadOnlyMemory<byte> Data, ImageFormat Format) : IToolInput
{
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>展示名（文件名 /「剪贴板图片」/「截图」）；单张工具可以忽略。</summary>
    public string Name { get; init; } = string.Empty;

    public int ByteSize => Data.Length;
}

/// <summary>
/// 一组图片（1..N 张）。批量识别、批量处理用。
///
/// 为什么不是 <c>FileSetInput</c>：图片已经从磁盘读成字节，插件不该为了拿图再去碰文件；
/// 也是为什么单张工具仍然只收 <see cref="ImageInput"/>——批量只是多包一层。
/// </summary>
public sealed record ImageSetInput(IReadOnlyList<ImageInput> Images) : IToolInput
{
    public static ImageSetInput Empty { get; } = new(Array.Empty<ImageInput>());
}

/// <summary>
/// 文件 / 文件夹路径输入。只传路径，不读内容。
/// </summary>
public sealed record FileSetInput(IReadOnlyList<string> Paths) : IToolInput
{
    public static FileSetInput Empty { get; } = new(Array.Empty<string>());
}

/// <summary>
/// 无输入。用于「生成类」工具（如 GUID 生成）。
/// </summary>
public sealed record EmptyInput : IToolInput
{
    public static EmptyInput Instance { get; } = new();
}
