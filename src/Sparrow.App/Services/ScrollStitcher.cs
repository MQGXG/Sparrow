using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace Sparrow.App.Services;

/// <summary>追加一帧的结果。</summary>
internal enum AppendOutcome
{
    /// <summary>找到了重叠，已完成追加。</summary>
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
/// 长图不把每一行都留在内存：先用内存攒到 8 MB，超过后自动落到临时文件，
/// 生成 PNG 时再从文件按行读、边压边写 IDAT。临时文件是 DeleteOnClose，
/// 进程异常退出也会被系统清掉。
/// </summary>
internal sealed class ScrollStitcher : IDisposable
{
    /// <summary>内存里最多攒多少行数据；超过就转存磁盘。</summary>
    private const long DefaultMemoryBudget = 8L * 1024 * 1024;

    /// <summary>
    /// 长图的像素预算：320 MB。超过后截断，避免后续「复制 / OCR / 固定」
    /// 把整张图解出来时把进程撑爆。
    /// 320 MB 大约对应 1000px 宽 8 万行、4K 宽 2.1 万行。
    /// </summary>
    private const long MaxPixelBytes = 320L * 1024 * 1024;

    /// <summary>磁盘不可用时的兜底行数上限（旧行为）。</summary>
    private const int MaxMemoryRows = 20000;

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly int _width;
    private readonly int _stride;
    private readonly int _frameHeight;
    private readonly int _templateRows;
    private readonly long _memoryBudget;
    private readonly string _tempDirectory;
    private readonly List<byte[]> _rows = [];

    private SafeFileHandle? _file;
    private string? _tempPath;
    private long _memoryBytes;
    private bool _spillFailed;
    private bool _disposed;
    private ulong[]? _previousHashes;

    public ScrollStitcher(int width, int height)
        : this(width, height, DefaultMemoryBudget, Path.GetTempPath())
    {
    }

    /// <summary>测试用：可以调小内存预算，强制走落盘分支。</summary>
    internal ScrollStitcher(int width, int height, long memoryBudget, string tempDirectory)
    {
        _width = width;
        _stride = width * 4;
        _frameHeight = height;
        _memoryBudget = Math.Max(0, memoryBudget);
        _tempDirectory = tempDirectory;

        // 模板太短容易误匹配，太长则限制单次可滚动的距离
        _templateRows = Math.Clamp(height / 4, 40, 160);
    }

    /// <summary>当前已拼出的总行数。</summary>
    public int TotalRows { get; private set; }

    /// <summary>是否已经转存到临时文件（调试 / 测试用）。</summary>
    internal bool UsesDisk => _file is not null;

    private int MaxRows => (int)Math.Max(1, MaxPixelBytes / _stride);

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

        var before = TotalRows;
        AppendRows(frame, firstNewRow, _frameHeight);
        _previousHashes = hashes;
        addedRows = TotalRows - before;
        return AppendOutcome.Added;
    }

    /// <summary>把累积的行编码成 PNG。没有内容时返回 null。</summary>
    public byte[]? BuildPng()
    {
        if (TotalRows == 0)
            return null;

        using var output = new MemoryStream();

        StreamingPngEncoder.Write(output, _width, TotalRows, ReadRow);

        return output.ToArray();
    }

    /// <summary>
    /// 生成缩小后的预览 PNG（整张长图按比例缩进 maxWidth × maxHeight）。
    /// 采样行列，不读整图；滚动截图时用来显示实时缩略图。
    /// </summary>
    public byte[]? BuildPreviewPng(int maxWidth, int maxHeight)
    {
        if (TotalRows == 0 || maxWidth <= 0 || maxHeight <= 0)
            return null;

        var scale = Math.Min(
            1.0,
            Math.Min((double)maxWidth / _width, (double)maxHeight / TotalRows));

        var targetWidth = Math.Max(1, (int)Math.Round(_width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(TotalRows * scale));

        var pixels = new byte[targetWidth * targetHeight * 4];
        var source = new byte[_stride];

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceRow = Math.Min(TotalRows - 1, y * TotalRows / targetHeight);
            ReadRow(sourceRow, source);

            var targetOffset = y * targetWidth * 4;

            for (var x = 0; x < targetWidth; x++)
            {
                var sourceColumn = Math.Min(_width - 1, x * _width / targetWidth) * 4;
                var targetIndex = targetOffset + x * 4;

                pixels[targetIndex] = source[sourceColumn];
                pixels[targetIndex + 1] = source[sourceColumn + 1];
                pixels[targetIndex + 2] = source[sourceColumn + 2];
                pixels[targetIndex + 3] = 255;
            }
        }

        return ScreenCapture.EncodePng(pixels, targetWidth, targetHeight);
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
        for (var y = from; y < to; y++)
        {
            if (TotalRows >= MaxRows)
                break;

            AppendRow(frame.AsSpan(y * _stride, _stride));
        }
    }

    private void AppendRow(ReadOnlySpan<byte> row)
    {
        if (_file is null && !_spillFailed && _memoryBytes + _stride > _memoryBudget)
            SpillToDisk();

        if (_file is not null)
        {
            RandomAccess.Write(_file, row, (long)TotalRows * _stride);
            TotalRows++;
            return;
        }

        if (_rows.Count >= MaxMemoryRows)
            return;

        _rows.Add(row.ToArray());
        _memoryBytes += _stride;
        TotalRows++;
    }

    private void SpillToDisk()
    {
        try
        {
            _tempPath = Path.Combine(
                _tempDirectory, $"sparrow-scroll-{Guid.NewGuid():N}.rows");

            _file = File.OpenHandle(
                _tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.DeleteOnClose | FileOptions.RandomAccess);

            long offset = 0;
            foreach (var row in _rows)
            {
                RandomAccess.Write(_file, row, offset);
                offset += _stride;
            }

            _rows.Clear();
            _memoryBytes = 0;
        }
        catch
        {
            // 临时目录不可写 / 磁盘满：退回内存，按旧上限截断
            _spillFailed = true;
            _file?.Dispose();
            _file = null;

            if (_tempPath is not null)
            {
                try
                {
                    File.Delete(_tempPath);
                }
                catch
                {
                    // 删不掉也不影响截图本身
                }
            }
        }
    }

    private void ReadRow(int index, Span<byte> destination)
    {
        if (_file is not null)
        {
            var read = RandomAccess.Read(
                _file, destination[.._stride], (long)index * _stride);

            if (read < _stride)
                throw new IOException("滚动截图临时文件不完整");

            return;
        }

        _rows[index].AsSpan(0, _stride).CopyTo(destination);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _rows.Clear();
        _file?.Dispose();
        _file = null;
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

/// <summary>逐行读取源数据：目的地至少要有 <c>width * 4</c> 字节。</summary>
internal delegate void RowReader(int index, Span<byte> destination);

/// <summary>
/// 流式 PNG 编码器。
///
/// Skia 的 Encode 需要先建一张完整位图；对几万行的长图来说，
/// 这一张位图就是几百 MB。这里按行读 BGRA、转 RGBA、zlib 压缩，
/// 每次只保留一行和 64 KB 的 IDAT 缓冲。
///
/// 输出是标准 PNG：8 位 RGBA，filter 0，IDAT 分块。
/// </summary>
internal static class StreamingPngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Write(Stream output, int width, int height, RowReader readRow)
    {
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // color type: RGBA
        header[10] = 0;  // compression
        header[11] = 0;  // filter
        header[12] = 0;  // interlace

        WriteChunk(output, "IHDR"u8, header);

        var row = new byte[1 + width * 4];

        using (var idat = new IdatStream(output))
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (var y = 0; y < height; y++)
                {
                    readRow(y, row.AsSpan(1));

                    // 源是 BGRA；PNG 要 RGBA。屏幕截图的 alpha 不可信，统一写成不透明。
                    for (var x = 1; x < row.Length; x += 4)
                    {
                        var blue = row[x];
                        row[x] = row[x + 2];
                        row[x + 2] = blue;
                        row[x + 3] = 255;
                    }

                    zlib.Write(row);
                }
            }

            idat.Flush();
        }

        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);

        output.Write(type);
        output.Write(data);

        var crc = Crc32.Compute(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    /// <summary>把 zlib 输出切成 64 KB 的 IDAT；不缓存整张压缩结果。</summary>
    private sealed class IdatStream : Stream
    {
        private const int ChunkSize = 64 * 1024;

        private readonly Stream _output;
        private readonly byte[] _buffer = new byte[ChunkSize];
        private int _length;

        public IdatStream(Stream output) => _output = output;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var take = Math.Min(ChunkSize - _length, buffer.Length);
                buffer[..take].CopyTo(_buffer.AsSpan(_length));
                _length += take;
                buffer = buffer[take..];

                if (_length == ChunkSize)
                    Flush();
            }
        }

        public override void Flush()
        {
            if (_length == 0)
                return;

            WriteChunk(_output, "IDAT"u8, _buffer.AsSpan(0, _length));
            _length = 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }
}

/// <summary>PNG 用的 CRC-32（多项式 0xEDB88320）。</summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        crc = Update(crc, first);
        crc = Update(crc, second);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

        return crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];

        for (var i = 0; i < table.Length; i++)
        {
            var value = (uint)i;

            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;

            table[i] = value;
        }

        return table;
    }
}
