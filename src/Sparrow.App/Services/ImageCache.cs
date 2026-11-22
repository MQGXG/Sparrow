using Avalonia.Media.Imaging;

namespace Sparrow.App.Services;

/// <summary>
/// 解码位图的统一缓存：解码尺寸、按字节的 LRU、失败标记、统计。
///
/// 为什么需要它：图片原始字节在磁盘只有几 MB，但按原尺寸解码后是
/// 宽×高×4 字节——你机器上 3 张 4K 截图解码后就是 95 MB。
/// 列表显示限高 120px，却解码 2160px 高，放大 18 倍，全靠 Image 控件缩小，
/// 而 Avalonia 的 Bitmap 是 native 内存，GC 不知道它占多少、不会因它提前回收，
/// 分配快过 finalizer 回收，工作集就只涨不跌。
///
/// 两个参考实现：
/// · LumaShot 用 WIC 的 CreateBitmapScaler 直接出小图，从不生成全尺寸位图；
///   缓存按字节 LRU（预览 8MB / 列表缩略图 384KB），见 preview_cache.h:9。
/// · QcClip 同样在解码阶段就缩（image_preview.rs:586-599 的 ratio 只缩不放），
///   但它靠「完全虚拟化 + 每帧按视口 trim」来限流——我们列表不是虚拟化的，
///   所以用 LumaShot 的按字节 LRU。
///
/// 淘汰时不 Dispose：被淘汰的位图可能仍被某个 Image 控件引用着，
/// 主动释放会让渲染崩溃。丢给 finalizer，数量有字节上限兜底。
/// </summary>
public sealed class ImageCache
{
    public static ImageCache Instance { get; } = new();

    /// <summary>列表缩略图：显示限高 120px，2 倍过采样（QcClip 的 IMAGE_PREVIEW_OVERSAMPLE 也是 2）。</summary>
    private const int ThumbnailHeight = 240;

    /// <summary>详情大图：详情页在 440 宽的面板里，800px 已是 2 倍过采样（LumaShot 预览窗口用 640px）。</summary>
    private const int DetailWidth = 800;

    /// <summary>缩略图缓存上限：240px 高的图约 400KB，16MB ≈ 40 张。</summary>
    private const long ThumbnailLimit = 16L * 1024 * 1024;

    /// <summary>详情缓存上限：800px 宽约 1.4MB，8MB ≈ 6 张。详情一次只看一张，够用。</summary>
    private const long DetailLimit = 8L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<ulong, LinkedListNode<Entry>> _thumbnail = [];
    private readonly LinkedList<Entry> _thumbnailOrder = [];
    private readonly Dictionary<ulong, LinkedListNode<Entry>> _detail = [];
    private readonly LinkedList<Entry> _detailOrder = [];
    private readonly HashSet<ulong> _failed = [];

    private long _thumbnailBytes;
    private long _detailBytes;

    private sealed record Entry(ulong Key, Bitmap Bitmap, long Bytes);

    /// <summary>列表缩略图：按内容缓存，解码到 <see cref="ThumbnailHeight"/> 高。</summary>
    public Bitmap? GetThumbnail(byte[] png) =>
        Get(png, ThumbnailHeight, ThumbnailLimit, _thumbnail, _thumbnailOrder, ref _thumbnailBytes, toHeight: true);

    /// <summary>详情大图：按内容缓存，解码到 <see cref="DetailWidth"/> 宽。</summary>
    public Bitmap? GetDetail(byte[] png) =>
        Get(png, DetailWidth, DetailLimit, _detail, _detailOrder, ref _detailBytes, toHeight: false);

    /// <summary>
    /// 原尺寸解码，不缓存。调用方负责释放——只给「用户主动放大看」的场景用，
    /// 比如图片缩放窗口，那里本来就该看到原始细节。
    /// </summary>
    public static Bitmap? DecodeOriginal(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch
        {
            // 损坏的图片不该让整个列表渲染失败
            return null;
        }
    }

    private Bitmap? Get(
        byte[] png,
        int size,
        long limit,
        Dictionary<ulong, LinkedListNode<Entry>> map,
        LinkedList<Entry> order,
        ref long bytes,
        bool toHeight)
    {
        var key = Hash(png);

        lock (_gate)
        {
            if (_failed.Contains(key))
                return null;

            if (map.TryGetValue(key, out var node))
            {
                // 命中即提到队头
                order.Remove(node);
                order.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        Bitmap? bitmap;
        try
        {
            using var stream = new MemoryStream(png);
            bitmap = toHeight
                ? Bitmap.DecodeToHeight(stream, size, BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            // 解码失败记下来，避免每次渲染都重试同一张坏图
            lock (_gate)
            {
                _failed.Add(key);
            }

            return null;
        }

        if (bitmap is null)
            return null;

        // 解码后的实际字节：像素 × 4
        var estimated = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

        lock (_gate)
        {
            // 单张就超上限的直接不缓存（LumaShot 的 preview_cache.h:20 同样处理）
            if (estimated <= limit)
            {
                var entry = new Entry(key, bitmap, estimated);
                var added = order.AddFirst(entry);
                map[key] = added;
                bytes += estimated;

                // 按字节 LRU 淘汰最老的；不 Dispose，可能还有控件引用着
                while (bytes > limit && order.Last is not null)
                {
                    var oldest = order.Last.Value;
                    order.RemoveLast();
                    map.Remove(oldest.Key);
                    bytes -= oldest.Bytes;
                }
            }
        }

        return bitmap;
    }

    /// <summary>
    /// 丢掉所有缓存引用。不主动 Dispose：控件可能仍持有位图，交给 finalizer。
    /// 在窗口隐藏、视觉树即将卸载时调用，让下一次打开重新解码。
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _thumbnail.Clear();
            _thumbnailOrder.Clear();
            _detail.Clear();
            _detailOrder.Clear();
            _thumbnailBytes = 0;
            _detailBytes = 0;
            _failed.Clear();
        }
    }

    /// <summary>缓存现状，写进日志用——下次再遇到内存问题不用翻代码算。</summary>
    public (int ThumbnailEntries, long ThumbnailBytes, int DetailEntries, long DetailBytes, int Failed) Stats()
    {
        lock (_gate)
        {
            return (_thumbnail.Count, _thumbnailBytes, _detail.Count, _detailBytes, _failed.Count);
        }
    }

    /// <summary>
    /// 内容哈希而不是引用：Refresh() 每次重建 VM、重新读文件，
    /// 同一张图每次都是新的 byte[] 实例，按引用缓存会永远不命中。
    /// FNV-1a 对 760KB 约 0.5ms，比解码（几十 ms）便宜两个数量级。
    /// </summary>
    private static ulong Hash(byte[] data)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}
