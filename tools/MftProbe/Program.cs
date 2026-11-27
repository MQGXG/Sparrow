using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MftProbe;

/// <summary>
/// 最小 MFT 可行性探针：只用管理员权限顺序读 $MFT，统计条目数和耗时。
/// 不重建路径、不建索引，目的是回答「这台机器上 MFT 扫描有多快、能不能读」。
/// </summary>
internal static class Program
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var drive = (args.Length > 0 ? args[0] : "C").TrimEnd(':', '\\');
        var outputPath = Path.Combine(Path.GetTempPath(), $"mft-probe-{drive}.txt");

        using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8)
        {
            AutoFlush = true
        };

        void Log(string message)
        {
            Console.WriteLine(message);
            writer.WriteLine(message);
        }

        void LogError(string message)
        {
            Console.Error.WriteLine(message);
            writer.WriteLine("ERROR: " + message);
        }

        Log($"MFT 探针：{drive}:");
        Log($"输出文件：{outputPath}");

        using var handle = CreateFile(
            $@"\\.\{drive}:",
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            LogError($"打开卷失败：Win32 {error}（{new Win32Exception(error).Message}）");
            LogError("读 $MFT 需要管理员权限，请以管理员身份运行本程序。");
            return 5;
        }

        var volumeData = new byte[512];

        if (!DeviceIoControl(
                handle,
                FsctlGetNtfsVolumeData,
                IntPtr.Zero,
                0,
                volumeData,
                (uint)volumeData.Length,
                out _,
                IntPtr.Zero))
        {
            LogError($"FSCTL_GET_NTFS_VOLUME_DATA 失败：Win32 {Marshal.GetLastWin32Error()}");
            return 6;
        }

        var bytesPerSector = BitConverter.ToUInt32(volumeData, 40);
        var bytesPerCluster = BitConverter.ToUInt32(volumeData, 44);
        var recordSize = BitConverter.ToUInt32(volumeData, 48);
        var mftValidLength = BitConverter.ToInt64(volumeData, 56);
        var mftStartLcn = BitConverter.ToInt64(volumeData, 64);

        Log($"扇区 {bytesPerSector} B，簇 {bytesPerCluster} B，记录 {recordSize} B");
        Log($"MFT 有效长度 {mftValidLength:N0} B，起始 LCN {mftStartLcn:N0}");

        if (recordSize == 0 || bytesPerCluster == 0)
        {
            LogError("卷几何数据异常，无法继续。");
            return 7;
        }

        // $MFT 自己的第一条记录里有它自己的 $DATA runlist
        var record0 = new byte[recordSize];

        if (!ReadAt(handle, mftStartLcn * bytesPerCluster, record0, (int)recordSize))
        {
            LogError("读取 $MFT 记录 0 失败。");
            return 8;
        }

        MftParser.ApplyFixup(record0, bytesPerSector, 0, (int)recordSize);
        var extents = MftParser.ParseDataRuns(record0);

        // $MFT 可能带 $ATTRIBUTE_LIST：扩展记录里还有更多 $DATA run
        var extensionIndexes = MftParser.ParseAttributeListRecordIndexes(
            record0,
            0x80,
            (offset, buffer, count) => ReadAt(handle, offset, buffer, count),
            bytesPerCluster);

        foreach (var index in extensionIndexes)
        {
            var offset = GetMftRecordVolumeOffset(
                index, extents, bytesPerCluster, recordSize);

            if (offset < 0)
                continue;

            var extension = new byte[recordSize];

            if (!ReadAt(handle, offset, extension, (int)recordSize))
                continue;

            MftParser.ApplyFixup(extension, bytesPerSector, 0, (int)recordSize);
            MftParser.ParseDataRunsInto(extension, extents);
        }

        if (extents.Count == 0)
        {
            LogError("解析 $MFT runlist 失败。");
            return 9;
        }

        var totalClusters = extents.Sum(extent => extent.clusters);
        Log($"$MFT run 数：{extents.Count}");
        Log($"$MFT 占用 {totalClusters:N0} 簇 ≈ {totalClusters * bytesPerCluster / 1024 / 1024:N1} MB");

        // 顺序扫描：8 MB 一块，逐条记录解析
        var stopwatch = Stopwatch.StartNew();
        long processedBytes = 0;
        long inUseRecords = 0;
        long nameCount = 0;
        long fileCount = 0;
        long dirCount = 0;
        var samples = new List<string>(10);
        var names = new List<(UInt128 Parent, string Name, long Size)>(4);
        var entries = new List<(ulong Index, ulong Parent, string Name, bool IsDir)>(2_000_000);
        var buffer = new byte[8 * 1024 * 1024];
        var remaining = mftValidLength > 0 ? mftValidLength : long.MaxValue;

        foreach (var (lcn, clusters) in extents)
        {
            if (remaining <= 0)
                break;

            var extentBytes = Math.Min(clusters * bytesPerCluster, remaining);
            var offset = lcn * bytesPerCluster;

            while (extentBytes > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, extentBytes);
                chunk -= chunk % (int)recordSize;

                if (chunk <= 0)
                    break;

                if (!ReadAt(handle, offset, buffer, chunk))
                    break;

                for (var r = 0; r + recordSize <= chunk; r += (int)recordSize)
                {
                    var recordIndex = processedBytes / recordSize;
                    processedBytes += recordSize;

                    if (buffer[r] != (byte)'F'
                        || buffer[r + 1] != (byte)'I'
                        || buffer[r + 2] != (byte)'L'
                        || buffer[r + 3] != (byte)'E')
                    {
                        continue;
                    }

                    MftParser.ApplyFixup(buffer, bytesPerSector, r, (int)recordSize);

                    var flags = BitConverter.ToUInt16(buffer, r + 0x16);

                    if ((flags & 0x01) == 0) // 记录未使用
                        continue;

                    inUseRecords++;
                    names.Clear();

                    var stdAttrs = MftParser.CollectNames(
                        buffer, r, (int)recordSize, names,
                        out _, out _, out _);

                    if (names.Count == 0)
                        continue;

                    nameCount += names.Count;

                    var isDirectory = (flags & 0x02) != 0
                        || (stdAttrs & (uint)FileAttributes.Directory) != 0;

                    if (isDirectory)
                        dirCount++;
                    else
                        fileCount++;

                    foreach (var (parent, name, _) in names)
                    {
                        if (samples.Count < 10)
                            samples.Add(name);

                        // 父引用是完整 FRN，低 48 位就是父记录索引
                        if (entries.Count < 2_000_000)
                        {
                            entries.Add((
                                (ulong)recordIndex,
                                (ulong)(parent & 0xFFFF_FFFF_FFFF),
                                name,
                                isDirectory));
                        }
                    }
                }

                offset += chunk;
                extentBytes -= chunk;
                remaining -= chunk;

                if (stopwatch.Elapsed.TotalSeconds > 300)
                {
                    LogError("超过 300 秒，提前停止。");
                    break;
                }
            }
        }

        stopwatch.Stop();

        Log(string.Empty);
        Log($"扫描完成：{stopwatch.Elapsed.TotalSeconds:F2} s");
        Log($"处理字节：{processedBytes:N0}");
        Log($"有效记录：{inUseRecords:N0}");
        Log($"文件：{fileCount:N0}，目录：{dirCount:N0}，文件名总数（含硬链接）：{nameCount:N0}");

        if (stopwatch.Elapsed.TotalSeconds > 0)
            Log($"速度：{inUseRecords / stopwatch.Elapsed.TotalSeconds:N0} 条/秒");

        // 路径重建验证：recordIndex → (parentIndex, name)，沿父链向上拼到根
        var byIndex = new Dictionary<ulong, (ulong Parent, string Name)>(entries.Count);

        foreach (var entry in entries)
            byIndex.TryAdd(entry.Index, (entry.Parent, entry.Name));

        var rebuilt = new List<string>(5);

        foreach (var entry in entries)
        {
            if (entry.IsDir)
                continue;

            var segments = new List<string>(8);
            var current = entry.Index;
            var parent = entry.Parent;
            var name = entry.Name;
            var depth = 0;

            while (depth++ < 64)
            {
                segments.Add(name);

                if (parent == current || !byIndex.TryGetValue(parent, out var node))
                    break;

                current = parent;
                parent = node.Parent;
                name = node.Name;
            }

            segments.Reverse();
            rebuilt.Add($"{drive}:\\" + string.Join("\\", segments));

            if (rebuilt.Count >= 5)
                break;
        }

        Log($"路径重建样例（字典 {byIndex.Count:N0} 条）：");

        foreach (var path in rebuilt)
            Log($"  {path}");

        Log("样例：");

        foreach (var name in samples)
            Log($"  {name}");

        return 0;
    }

    private static long GetMftRecordVolumeOffset(
        ulong recordIndex,
        List<(long lcn, long clusters)> extents,
        uint bytesPerCluster,
        uint recordSize)
    {
        var target = (long)recordIndex * recordSize;
        long current = 0;

        foreach (var (lcn, clusters) in extents)
        {
            var extentBytes = clusters * bytesPerCluster;

            if (target >= current && target < current + extentBytes)
                return lcn * bytesPerCluster + (target - current);

            current += extentBytes;
        }

        return -1;
    }

    private static bool ReadAt(
        SafeFileHandle handle, long offset, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(handle, offset, out _, 0))
            return false;

        var done = 0;

        while (done < count)
        {
            var slice = done == 0 ? buffer : new byte[count - done];

            if (!ReadFile(handle, slice, (uint)(count - done), out var read, IntPtr.Zero)
                || read == 0)
            {
                return false;
            }

            if (done != 0)
                Array.Copy(slice, 0, buffer, done, (int)read);

            done += (int)read;
        }

        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        byte[] outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle file,
        long distanceToMove,
        out long newFilePointer,
        uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle file,
        byte[] buffer,
        uint numberOfBytesToRead,
        out uint numberOfBytesRead,
        IntPtr overlapped);
}
