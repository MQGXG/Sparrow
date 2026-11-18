namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 本次执行可读的文件集合。
///
/// 只读、只覆盖「这次输入里出现的路径」：<see cref="ReadAllBytes"/> 对任何
/// 不在本次输入中的路径返回 null，因此插件依然不能遍历文件系统，也读不到用户的其他文件。
///
/// 为什么需要它：<see cref="Inputs.FileSetInput"/> 只传路径，插件又没有文件系统能力，
/// 文件类工具（批量识别、哈希、压缩包列表）就没法工作。
/// 宿主在这里扮演「受控代理」——和 <see cref="IClipboardHistory.LoadImage"/> 的思路一致。
///
/// 通常只在输入是 <see cref="Inputs.FileSetInput"/> 时非空，其他输入下插件必须判空。
/// </summary>
public interface IFileAccess
{
    /// <summary>路径是否属于本次输入，且当前存在可读文件。</summary>
    bool CanRead(string path);

    /// <summary>读取文件全部字节；路径越权、文件不存在或读取失败时返回 null（不抛异常）。</summary>
    byte[]? ReadAllBytes(string path);

    /// <summary>
    /// 打开只读流；路径越权、文件不存在或打开失败时返回 null（不抛异常）。
    ///
    /// 为什么还要流：哈希、压缩包列表这类工具要处理大文件，
    /// <see cref="ReadAllBytes"/> 会把整个文件读进内存。流由插件负责释放。
    /// </summary>
    Stream? OpenRead(string path);
}
