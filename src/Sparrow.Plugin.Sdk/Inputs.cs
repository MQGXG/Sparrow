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

    public int ByteSize => Data.Length;
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
