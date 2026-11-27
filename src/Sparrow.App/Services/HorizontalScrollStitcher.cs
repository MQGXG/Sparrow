namespace Sparrow.App.Services;

/// <summary>
/// 横向滚动截图拼接器。
///
/// 和纵向拼接器对称：把每一列压成一个哈希，用上一帧最后若干列作为模板，
/// 在新帧里找它出现的位置，再把内容区右侧新增的列追加到结果里。
/// 固定左栏 / 右栏（操作列、滚动条）的识别与校准也复用同一套逻辑。
///
/// 结果按列存放（每列 height*4 字节），生成 PNG 时再按行把各列拼起来。
/// 横向长图一般不会太长，这里只用内存；超过像素预算后停止追加，
/// 避免把进程撑爆。纵向那条链路仍然保留磁盘溢写。
/// </summary>
internal sealed class HorizontalScrollStitcher : IScrollStitcher
{
    /// <summary>
    /// 结果像素预算：320 MB。和纵向拼接器保持一致，
    /// 避免后续「复制 / OCR / 固定」把整张图解出来时把进程撑爆。
    /// </summary>
    private const long MaxPixelBytes = 320L * 1024 * 1024;

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>每帧可见的列数（选区宽度）。</summary>
    private readonly int _frameWidth;

    /// <summary>结果高度（选区高度），也是每列的像素数。</summary>
    private readonly int _height;

    private readonly int _stride;

    /// <summary>模板列数：太短容易误匹配，太长则限制单次可滚动的距离。</summary>
    private readonly int _templateColumns;

    private readonly List<byte[]> _columns = [];

    private long _pixelBytes;

    private ulong[]? _previousHashes;

    /// <summary>校准出的固定右栏列数；0 表示没有（或还没校准）。</summary>
    private int _fixedRightColumns;

    /// <summary>固定右栏只校准一次：拿到第一段真实滚动后才可信。</summary>
    private bool _fixedCalibrated;

    /// <summary>超过像素预算后不再追加，避免无界增长。</summary>
    private bool _full;

    private bool _disposed;

    public HorizontalScrollStitcher(int frameWidth, int frameHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);

        _frameWidth = frameWidth;
        _height = frameHeight;
        _stride = frameWidth * 4;

        // 小选区时按帧宽收缩模板，避免模板超过一帧
        var minimumTemplateColumns = Math.Min(40, Math.Max(1, frameWidth / 2));
        _templateColumns = Math.Clamp(frameWidth / 4, minimumTemplateColumns, 160);
    }

    /// <summary>当前已拼出的总列数。</summary>
    public int TotalRows => _columns.Count;

    public AppendOutcome Append(byte[] frame, out int addedRows)
    {
        addedRows = 0;

        if (frame.Length < _stride * _height)
            return AppendOutcome.LostTracking;

        // 已经到像素预算上限：按「没滚动」处理，让捕获循环自然收尾
        if (_full)
            return AppendOutcome.Duplicate;

        var hashes = new ulong[_frameWidth];
        for (var x = 0; x < _frameWidth; x++)
            hashes[x] = HashColumn(frame, x);

        if (_previousHashes is null)
        {
            AppendColumns(frame, 0, _frameWidth);
            _previousHashes = hashes;
            addedRows = TotalRows;
            return AppendOutcome.Added;
        }

        var resolved = ResolveOffset(_previousHashes, hashes, out var offset, out var fromBands);

        if (resolved == OffsetOutcome.Lost)
            return AppendOutcome.LostTracking;

        if (resolved == OffsetOutcome.Still)
            return AppendOutcome.Duplicate;

        // 多段投票给出的结果先用重叠区交叉验证，避免重复内容误判
        if (fromBands
            && !ScrollOffsetMatcher.Verify(_previousHashes, hashes, offset, _frameWidth))
        {
            return AppendOutcome.LostTracking;
        }

        // 第一段真实滚动后校准固定右栏，避免它被一列列重复拼进结果
        CalibrateFixedRight(offset, hashes);

        var contentRight = _frameWidth - _fixedRightColumns;

        // 内容区已经整屏滚过去了，没有重叠可以验证
        if (offset >= contentRight)
            return AppendOutcome.LostTracking;

        var firstNewColumn = contentRight - offset;
        var newColumns = contentRight - firstNewColumn;

        if (newColumns <= 0)
            return AppendOutcome.Duplicate;

        var before = TotalRows;
        AppendColumns(frame, firstNewColumn, contentRight);
        _previousHashes = hashes;
        addedRows = TotalRows - before;
        return AppendOutcome.Added;
    }

    /// <summary>把累积的列编码成 PNG。没有内容时返回 null。</summary>
    public byte[]? BuildPng()
    {
        if (_columns.Count == 0)
            return null;

        using var output = new MemoryStream();

        // 宽 = 总列数，高 = 选区高度；每行由各列的第 index 个像素拼成
        StreamingPngEncoder.Write(output, _columns.Count, _height, ReadRow);

        return output.ToArray();
    }

    /// <summary>
    /// 生成缩小后的预览 PNG（整张长图按比例缩进 maxWidth × maxHeight）。
    /// 直接按采样列 / 行取像素，不先拼出整图。
    /// </summary>
    public byte[]? BuildPreviewPng(int maxWidth, int maxHeight)
    {
        if (_columns.Count == 0 || maxWidth <= 0 || maxHeight <= 0)
            return null;

        var scale = Math.Min(
            1.0,
            Math.Min((double)maxWidth / _columns.Count, (double)maxHeight / _height));

        var targetWidth = Math.Max(1, (int)Math.Round(_columns.Count * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(_height * scale));

        var pixels = new byte[targetWidth * targetHeight * 4];

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(_height - 1, y * _height / targetHeight) * 4;
            var targetRow = y * targetWidth * 4;

            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(_columns.Count - 1, x * _columns.Count / targetWidth);
                var column = _columns[sourceX];
                var target = targetRow + x * 4;

                pixels[target] = column[sourceY];
                pixels[target + 1] = column[sourceY + 1];
                pixels[target + 2] = column[sourceY + 2];
                pixels[target + 3] = 255;
            }
        }

        return ScreenCapture.EncodePng(pixels, targetWidth, targetHeight);
    }

    /// <summary>先试「内容区右侧模板」，失败或看起来没动时再用多段投票确认。</summary>
    private OffsetOutcome ResolveOffset(
        ulong[] previous, ulong[] current, out int offset, out bool fromBands)
    {
        offset = 0;
        fromBands = false;

        // 校准出固定右栏后，模板要避开它，从真正的内容区右侧往左取
        var templateStart = Math.Max(0, _frameWidth - _fixedRightColumns - _templateColumns);
        var (bestPosition, bestScore) = ScrollOffsetMatcher.FindBestMatch(
            previous, current, templateStart, _templateColumns);

        if (bestPosition >= 0 && bestScore >= _templateColumns - 2)
        {
            offset = templateStart - bestPosition;

            if (offset > 0)
                return OffsetOutcome.Moved;

            // 模板命中右缘不等于没滚动：固定右栏也会这样
            var confirmed = ScrollOffsetMatcher.EstimateOffset(previous, current, _frameWidth);
            if (confirmed is > 0)
            {
                offset = confirmed.Value;
                fromBands = true;
                return OffsetOutcome.Moved;
            }

            return OffsetOutcome.Still;
        }

        var estimated = ScrollOffsetMatcher.EstimateOffset(previous, current, _frameWidth);
        if (estimated is null)
            return OffsetOutcome.Lost;

        if (estimated.Value <= 0)
            return OffsetOutcome.Still;

        offset = estimated.Value;
        fromBands = true;
        return OffsetOutcome.Moved;
    }

    /// <summary>
    /// 校准固定右栏。只在第一段真实滚动后做一次：
    /// 此时画布还只有首帧，回退掉右栏不会破坏内容顺序。
    /// </summary>
    private void CalibrateFixedRight(int offset, ulong[] current)
    {
        if (_fixedCalibrated || offset < 8 || _previousHashes is null)
            return;

        _fixedCalibrated = true;

        var columns = ScrollOffsetMatcher.DetectFixedTrailing(
            _previousHashes, current, _frameWidth);

        if (columns < 8 || columns > _frameWidth / 2)
            return;

        // 纯色区域可能只是页面右侧的空白内容，不能当成固定右栏裁掉
        if (ScrollOffsetMatcher.DistinctCount(current, _frameWidth - columns, columns)
            < Math.Max(3, columns / 4))
        {
            return;
        }

        TrimTrailingColumns(columns);
        _fixedRightColumns = columns;
    }

    /// <summary>回退画布末尾的若干列（校准固定右栏时用）。</summary>
    private void TrimTrailingColumns(int count)
    {
        if (count <= 0 || count > _columns.Count)
            return;

        var remove = Math.Min(count, _columns.Count);
        _columns.RemoveRange(_columns.Count - remove, remove);
        _pixelBytes -= (long)remove * _height * 4;
    }

    private void AppendColumns(byte[] frame, int from, int to)
    {
        var columnBytes = _height * 4;

        for (var x = from; x < to; x++)
        {
            if (_pixelBytes + columnBytes > MaxPixelBytes)
            {
                _full = true;
                return;
            }

            var column = new byte[columnBytes];

            for (var y = 0; y < _height; y++)
            {
                var source = y * _stride + x * 4;
                var target = y * 4;

                column[target] = frame[source];
                column[target + 1] = frame[source + 1];
                column[target + 2] = frame[source + 2];
                column[target + 3] = 255;
            }

            _columns.Add(column);
            _pixelBytes += columnBytes;
        }
    }

    /// <summary>把第 index 行从各列里拼出来，交给流式 PNG 编码器。</summary>
    private void ReadRow(int index, Span<byte> destination)
    {
        var target = 0;

        foreach (var column in _columns)
        {
            var source = index * 4;

            destination[target] = column[source];
            destination[target + 1] = column[source + 1];
            destination[target + 2] = column[source + 2];
            destination[target + 3] = 255;
            target += 4;
        }
    }

    /// <summary>列哈希：忽略 alpha，避免截图 alpha 通道不一致造成误判。</summary>
    private ulong HashColumn(byte[] frame, int x)
    {
        var hash = FnvOffset;

        for (var y = 0; y < _height; y++)
        {
            var offset = y * _stride + x * 4;

            hash = (hash ^ frame[offset]) * FnvPrime;
            hash = (hash ^ frame[offset + 1]) * FnvPrime;
            hash = (hash ^ frame[offset + 2]) * FnvPrime;
        }

        return hash;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _columns.Clear();
    }
}
