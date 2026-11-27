using System.Diagnostics;
using System.Text;
using Lertaro.Core;
using Lertaro.Core.Indexer.Usn.Journal;
using Lertaro.Core.IndexV2;
using Lertaro.Core.IndexV2.Persistence;
using Lertaro.Core.IndexV2.Search;

namespace LertaroIndexProbe;

/// <summary>
/// 索引结构与查询性能探针：复用 Lertaro 的 MFT 扫描、列式 Snapshot、查询实现，
/// 在一台真实机器上测「178 万条记录能不能建得起来、查得快不快」。
///
/// 程序集名借用 Lertaro.Core.Tests 只是为了满足它的 InternalsVisibleTo；
/// 这是验证工具，不参与 Sparrow 构建。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var drive = (args.Length > 0 ? args[0] : "C").TrimEnd(':', '\\');
        var outputPath = Path.Combine(Path.GetTempPath(), $"lertaro-index-probe-{drive}.txt");
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"lertaro-index-{drive}.snapshot");

        using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8)
        {
            AutoFlush = true
        };

        void Log(string message)
        {
            Console.WriteLine(message);
            writer.WriteLine(message);
        }

        try
        {
            Log($"Lertaro 索引探针：{drive}:");
            Log($"输出：{outputPath}");
            Log($"Snapshot：{snapshotPath}");

            // 1. MFT 全量扫描
            var reader = new JournalReader();
            var scanWatch = Stopwatch.StartNew();
            var lastReport = 0L;

            var result = reader.IndexDrive(
                drive,
                null,
                (files, dirs) =>
                {
                    var now = Environment.TickCount64;

                    if (now - lastReport < 1000)
                        return;

                    lastReport = now;
                    Log($"  扫描中：文件 {files:N0}，目录 {dirs:N0}");
                },
                null,
                CancellationToken.None);

            scanWatch.Stop();

            if (result is null)
            {
                Log("扫描失败（缺少管理员权限，或卷不支持）。");
                return 5;
            }

            Log($"MFT 扫描：{scanWatch.Elapsed.TotalSeconds:F2} s，记录 {result.Store.Records.Count:N0}");
            Log($"托管内存（扫描后）：{GC.GetTotalMemory(false) / 1024.0 / 1024:F1} MB");

            // 2. 写成列式 Snapshot
            var writeWatch = Stopwatch.StartNew();
            SnapshotWriter.Write(result.Store, snapshotPath);
            writeWatch.Stop();

            var info = new FileInfo(snapshotPath);
            Log($"Snapshot 写入：{writeWatch.Elapsed.TotalSeconds:F2} s，{info.Length / 1024.0 / 1024:F1} MB");

            // 释放扫描阶段的 FileRecordStore，避免把它的内存算进查询基线
            result = null;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Log($"托管内存（释放扫描对象后）：{GC.GetTotalMemory(false) / 1024.0 / 1024:F1} MB");

            // 3. 打开并查询
            var snapshot = Snapshot.Open(snapshotPath);
            using var live = new LiveIndex(snapshot);

            Log($"行数：{snapshot.Count:N0}，唯一文件名：{snapshot.UniqueCount:N0}");

            // 预热：让 JIT 和首次初始化发生在计时之外
            IndexV2Searcher.SearchStreaming(
                live, "warmup", 10, _ => { }, CancellationToken.None);

            var queries = new[] { "report", "config", "sparrow", "文档", "a" };

            foreach (var query in queries)
            {
                var times = new List<double>(3);
                var sampleRows = new List<SearchResult>();

                for (var round = 0; round < 3; round++)
                {
                    live.SearchPages.Clear();

                    var rows = new List<SearchResult>();
                    var searchWatch = Stopwatch.StartNew();

                    IndexV2Searcher.SearchStreaming(
                        live, query, 50, row => rows.Add(row), CancellationToken.None);

                    searchWatch.Stop();
                    times.Add(searchWatch.Elapsed.TotalMilliseconds);

                    if (round == 0)
                        sampleRows = rows;
                }

                Log($"查询 \"{query}\"：min {times.Min():F1} / avg {times.Average():F1} ms，{sampleRows.Count} 条");

                foreach (var row in sampleRows.Take(3))
                    Log($"  {row.Name} | {row.Path}");
            }

            // 4. 页缓存前后
            live.SearchPages.Clear();
            var cold = Measure(live, "config", 50);
            var warm = Measure(live, "config", 50);
            Log($"缓存前后（config）：{cold:F1} ms / {warm:F1} ms");

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Log($"托管内存（查询后）：{GC.GetTotalMemory(false) / 1024.0 / 1024:F1} MB");

            using var process = Process.GetCurrentProcess();
            Log($"进程工作集：{process.WorkingSet64 / 1024.0 / 1024:F1} MB");

            return 0;
        }
        catch (Exception ex)
        {
            Log($"失败：{ex}");
            return 1;
        }
    }

    private static double Measure(LiveIndex live, string query, int limit)
    {
        var rows = new List<SearchResult>();
        var watch = Stopwatch.StartNew();

        IndexV2Searcher.SearchStreaming(
            live, query, limit, row => rows.Add(row), CancellationToken.None);

        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }
}
