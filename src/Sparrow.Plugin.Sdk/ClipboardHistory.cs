namespace Sparrow.Plugin.Sdk;

/// <summary>剪贴板条目的内容形态。</summary>
public enum ClipboardEntryKind
{
    Text,

    /// <summary>图片条目：宿主统一存成 PNG，用 <see cref="IClipboardHistory.LoadImage"/> 取回字节。</summary>
    Image,

    /// <summary>从资源管理器复制的一个或多个文件路径。</summary>
    Files
}

/// <summary>
/// 一条剪贴板历史记录。
///
/// 文本条目全文放在 <see cref="Text"/>（超长截断）；
/// 图片条目只有元数据，内容经 <see cref="IClipboardHistory.LoadImage"/> 按需取回；
/// 文件条目只有路径列表——宿主不复制文件内容。
/// </summary>
public sealed record ClipboardHistoryEntry
{
    public required string Id { get; init; }

    public required ClipboardEntryKind Kind { get; init; }

    /// <summary>列表展示用的单行摘要，按内容形态生成。</summary>
    public required string Preview { get; init; }

    /// <summary>文本条目的全文；其他形态为 null。</summary>
    public string? Text { get; init; }

    /// <summary>文件条目的路径列表；其他形态为空。</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>原始内容大小：文本按字符数，图片按 PNG 字节数。</summary>
    public long ByteSize { get; init; }
}

/// <summary>
/// 宿主记录的剪贴板历史。
///
/// 监听与落盘都在宿主：插件不碰 Win32、不碰文件系统，
/// 拿到的是已经整理好的条目列表；图片内容经 <see cref="LoadImage"/> 按需取回。
/// 剪贴板历史可能被用户关闭（隐私），此时 <see cref="IToolHost.ClipboardHistory"/> 为 null，
/// 插件必须先做 null 检查并给出提示，不能假设它一定存在。
/// </summary>
public interface IClipboardHistory
{
    /// <summary>当前记录条数。</summary>
    int Count { get; }

    /// <summary>最近的记录，新的在前。</summary>
    IReadOnlyList<ClipboardHistoryEntry> Recent(int count);

    /// <summary>按关键字过滤（匹配文本内容、摘要和文件路径），新的在前。</summary>
    IReadOnlyList<ClipboardHistoryEntry> Search(string keyword, int count);

    /// <summary>取回图片条目的 PNG 字节。条目不是图片或文件已被清理时返回 null。</summary>
    byte[]? LoadImage(ClipboardHistoryEntry entry);
}
