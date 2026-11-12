using SkiaSharp;

namespace Sparrow.App.Services;

/// <summary>追加一帧的结果。</summary>
internal enum AppendOutcome
{
    /// <summary>找到了重叠，已追加新内容。</summary>
    Added,

    /// <summary>和上一帧一样，页面没有滚动。</summary>
    Duplicate,

    /// <summary>找不到重叠（滚动过快或页面跳变），应停止捕获。</summary>
    LostTracking
}

/// <summary>
/// 滚动截图拼接器。
///
/// 只接收原始 BGRA 帧（top-down，stride = width*4），不碰 UI。
/// 做法：用上一帧最后 T 行作为模板，在下一帧里找它出现的位置；
/// 找到后把模板之后的行追加到结果里。
///
/// 为什么用行哈希而不是逐像素比较：
/// 1000×800 的一帧做全像素匹配要几亿次比较，跟不上 200ms 的捕获节奏。
/// 行哈希把每帧匹配压到「行数 × 模板行数」次整数比较。
/// </summary>
internal sealed class ScrollStitcher
{
    /// <summary>长图上限，防止用户一直滚导致内存爆掉。</summary>
    private const int MaxRows = 20000;

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly int _width;
    private readonly int _stride;
    private readonly int _frameHeight;
    private readonly int _templateRows;
    private readonly List<byte[]> _rows = [];

    private ulong[]? _previousHashes;

    public ScrollStitcher(int width, int height)
    {
        _width = width;
        _stride = width * 4;
        _frameHeight = height;

        // 模板太短容易误匹配，太长则限制单次可滚动的距离
        _templateRows = Math.Clamp(height / 4, 40, 160);
    }

    /// <summary>当前已拼出的总行数。</summary>
    public int TotalRows => _rows.Count;

    public AppendOutcome Append(byte[] frame, out int addedRows)
    {
        addedRows = 0;

        if (frame.Length < _stride * _frameHeight)
            return AppendOutcome.LostTracking;

        var hashes = new ulong[_frameHeight];
        for (var y = 0; y < _frameHeight; y++)
            hashes[y] = HashRow(frame.AsSpan(y * _stride, _stride));

        if (_previousHashes is null)
        {
            AppendRows(frame, 0, _frameHeight);
            _previousHashes = hashes;
            addedRows = _frameHeight;
            return AppendOutcome.Added;
        }

        var (bestY, bestScore) = FindBestMatch(_previousHashes, hashes);

        // 允许极少数行不同：光标闪烁、局部动画
        if (bestScore < _templateRows - 2)
            return AppendOutcome.LostTracking;

        // 模板仍然落在帧底 = 页面没动
        if (bestY >= _frameHeight - _templateRows)
            return AppendOutcome.Duplicate;

        var firstNewRow = bestY + _templateRows;
        var newRows = _frameHeight - firstNewRow;

        if (newRows <= 0)
            return AppendOutcome.Duplicate;

        AppendRows(frame, firstNewRow, _frameHeight);
        _previousHashes = hashes;
        addedRows = newRows;
        return AppendOutcome.Added;
    }

    /// <summary>把累积的行编码成 PNG。没有内容时返回 null。</summary>
    public byte[]? BuildPng()
    {
        if (_rows.Count == 0)
            return null;

        var info = new SKImageInfo(_width, _rows.Count, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);

        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        var copyLength = Math.Min(_stride, rowBytes);

        for (var i = 0; i < _rows.Count; i++)
            _rows[i].AsSpan(0, copyLength).CopyTo(pixels.Slice(i * rowBytes, copyLength));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>
    /// 从下往上扫，优先选「滚动量最小」的匹配：
    /// 页面里出现重复内容时，避免一次跳很远拼进重复片段。
    /// </summary>
    private (int Y, int Score) FindBestMatch(ulong[] previous, ulong[] current)
    {
        var templateStart = _frameHeight - _templateRows;
        var bestY = -1;
        var bestScore = 0;

        for (var y = templateStart; y >= 0; y--)
        {
            var score = 0;
            for (var i = 0; i < _templateRows; i++)
            {
                if (current[y + i] == previous[templateStart + i])
                    score++;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestY = y;

                if (score == _templateRows)
                    break;
            }
        }

        return (bestY, bestScore);
    }

    private void AppendRows(byte[] frame, int from, int to)
    {
        for (var y = from; y < to && _rows.Count < MaxRows; y++)
            _rows.Add(frame.AsSpan(y * _stride, _stride).ToArray());
    }

    /// <summary>行哈希：忽略 alpha，避免截图 alpha 通道不一致造成误判。</summary>
    private static ulong HashRow(ReadOnlySpan<byte> row)
    {
        var hash = FnvOffset;

        for (var i = 0; i + 3 < row.Length; i += 4)
        {
            hash = (hash ^ row[i]) * FnvPrime;
            hash = (hash ^ row[i + 1]) * FnvPrime;
            hash = (hash ^ row[i + 2]) * FnvPrime;
        }

        return hash;
    }
}
