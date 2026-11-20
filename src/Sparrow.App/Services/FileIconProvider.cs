using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;

namespace Sparrow.App.Services;

/// <summary>
/// 文件路径 → 文件类型图标（PNG 字节）。
///
/// 做法参考 QcClip 用的 <c>file_icon_provider 1.0.2</c>（src/implementation/windows.rs）：
/// 它用 <c>IShellItemImageFactory</c> + <c>SIIGBF_ICONONLY | SIIGBF_SCALEUP</c>，
/// 在一个专用线程里做 COM 调用（COM 对象不能跨线程，必须固定线程初始化和使用），
/// 并且**按扩展名缓存**——同扩展名的图标来自注册表的文件类型关联，彼此相同，
/// 几千个 .md 只查一次；<c>.exe</c> / <c>.lnk</c> 每个文件图标各不相同，
/// 所以按完整路径缓存。它甚至有 test_exe_icons_are_not_cached 专门锁这条。
///
/// 这里用 <see cref="Icon.ExtractAssociatedIcon(string)"/>：同样走注册表关联，
/// 能拿到 Word / Markdown / 程序的真图标，32px 对 18–38px 的卡片够用。
/// 不做 COM 接口是为了少一整套 interop；实机看着糊再换 IShellItemImageFactory。
///
/// 文件不存在直接返回 null 且不缓存：<see cref="File.Exists"/> 很快，
/// 而缓存失败会让「文件后来被创建」的条目永远拿不到图标。
/// </summary>
public static class FileIconProvider
{
    /// <summary>同扩展名共用一个图标。</summary>
    private static readonly ConcurrentDictionary<string, byte[]> ByExtension =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图标各不相同的两类：按完整路径缓存。</summary>
    private static readonly ConcurrentDictionary<string, byte[]> ByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取文件类型图标；文件不存在或取不到返回 null，由调用方回退到类型图标。</summary>
    public static byte[]? TryGet(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        string key;
        var perFile = false;

        try
        {
            var extension = Path.GetExtension(path);
            perFile = extension.Length == 0
                || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            key = perFile ? path : extension;
        }
        catch
        {
            // 路径含非法字符：退回按完整路径试一次
            key = path;
            perFile = true;
        }

        var cache = perFile ? ByPath : ByExtension;

        // 整段查-解-存必须原子：TryGetValue 与赋值之间没有锁的话，
        // 两个线程会各自解码出不同实例，缓存形同虚设
        lock (Gate)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;

            var png = Resolve(path);
            if (png is null)
                return null;

            cache[key] = png;

            // 图标缓存不设字节上限，但条数要有：.exe / .lnk 按完整路径存，
            // 长期运行会只增不减。LumaShot 是 16 条整表清（file_icons.h:18），
            // 我们的图标小得多，放宽到 256；清掉只是下次重新解码，不影响已显示的。
            if (perFile && ByPath.Count > MaxPathEntries)
                ByPath.Clear();

            return png;
        }
    }

    /// <summary>保护三个缓存容器的读写原子性。</summary>
    private static readonly object Gate = new();

    /// <summary>按路径缓存的条数上限（只约束 .exe / .lnk 这类每文件图标不同的）。</summary>
    private const int MaxPathEntries = 256;

    private static byte[]? Resolve(string path)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
                return null;

            using var source = icon.ToBitmap();
            using var stream = new MemoryStream();
            source.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            // 某些路径取不到图标不影响列表渲染
            return null;
        }
    }
}
