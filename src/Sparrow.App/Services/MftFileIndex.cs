using Lertaro.Core.Indexer.Usn;
using Lertaro.Core.IndexV2;
using Lertaro.Core.IndexV2.Persistence;
using Lertaro.Core.IndexV2.Search;
using Lertaro.Core.Services.Search;
using Lertaro.Core.Wire;

namespace Sparrow.App.Services;

/// <summary>
/// 基于 Windows 索引服务的 MFT 文件索引。
///
/// 服务（Sparrow.IndexService）以 SYSTEM 权限维护
/// <c>%ProgramData%\Sparrow\indexes\*.idx</c>：
/// MFT 全量扫描、USN 增量监听、空闲 compact 都在服务里完成。
/// 宿主只做三件事：mmap 打开 .idx、查询、监听文件变化后重新加载。
///
/// 这样宿主不需要管理员权限，也不需要自己维护 USN 监控。
/// </summary>
internal sealed class MftFileIndex : IFileIndex
{
    private readonly IAppLog _log;
    private readonly IReadOnlyList<string> _drives;
    private readonly string _indexDirectory;

    private readonly Dictionary<string, LiveIndex> _liveIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    /// <summary>索引盘列表；实际由服务维护，这里保留给设置页/状态展示。</summary>
    internal IReadOnlyList<string> Drives => _drives;

    private FileSystemWatcher? _watcher;
    private int _count;
    private int _reloadScheduled;
    private volatile bool _started;
    private volatile bool _indexing;
    private volatile bool _ready;
    private volatile string? _statusMessage;

    public MftFileIndex(
        IAppLog log,
        IEnumerable<string>? drives = null,
        string? indexDirectory = null)
    {
        _log = log;
        _drives = NormalizeDrives(drives ?? DiscoverDrives());

        _indexDirectory = indexDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Sparrow",
            "indexes");
    }

    public bool IsReady => _ready;

    public bool IsIndexing => _indexing;

    public int Count => Volatile.Read(ref _count);

    public string? StatusMessage => _statusMessage;

    public event EventHandler? Changed;

    public void Start()
    {
        if (_started)
            return;

        _started = true;
        _indexing = true;

        _ = Task.Run(() =>
        {
            ReloadAll();
            StartWatcher();

            _indexing = false;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// 重新读取索引目录里的所有 .idx。
    /// 服务 compact 时会原子替换文件；这里先构建新字典，再一次性换掉旧映射，
    /// 避免查询期间看到半更新的状态。
    /// </summary>
    private void ReloadAll()
    {
        var loaded = new Dictionary<string, (LiveIndex Live, int Rows)>(
            StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_indexDirectory))
        {
            try
            {
                foreach (var entry in LocalDriveCacheLocator.ListCachedDrives(_indexDirectory))
                {
                    try
                    {
                        var snapshot = Snapshot.Open(entry.Path);
                        loaded[entry.Drive] = (new LiveIndex(snapshot), snapshot.Count);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"加载 {entry.Drive} 盘索引失败：{entry.Path}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"读取索引目录失败：{_indexDirectory}（{ex.Message}）");
            }
        }

        lock (_gate)
        {
            foreach (var old in _liveIndexes.Values)
                old.Dispose();

            _liveIndexes.Clear();

            var total = 0;

            foreach (var (drive, value) in loaded)
            {
                _liveIndexes[drive] = value.Live;
                total += value.Rows;
            }

            _count = total;
        }

        _ready = _liveIndexes.Count > 0;
        _statusMessage = _ready ? null : "索引服务尚未建立索引，请安装/启动 Sparrow 索引服务";

        _log.Info($"MFT 索引已加载：{_count:N0} 条 / {_liveIndexes.Count} 个盘");
    }

    /// <summary>监听服务写入/替换 .idx；防抖后再重载，避免 compact 过程中反复打开。</summary>
    private void StartWatcher()
    {
        try
        {
            if (_watcher is not null)
                return;

            if (!Directory.Exists(_indexDirectory))
                return;

            _watcher = new FileSystemWatcher(_indexDirectory, "*.idx")
            {
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Deleted += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
        }
        catch (Exception ex)
        {
            _log.Warn($"监听索引目录失败：{ex.Message}");
        }
    }

    private void ScheduleReload()
    {
        if (Interlocked.Exchange(ref _reloadScheduled, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                ReloadAll();
                _indexing = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                Interlocked.Exchange(ref _reloadScheduled, 0);
            }
        });
    }

    public void Rebuild()
    {
        // 索引文件由服务持有和重建；宿主这里只重新读一次当前文件。
        // 安装服务后索引目录才出现，所以这里也顺便补上 watcher。
        StartWatcher();
        ReloadAll();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<FileSearchItem> Search(string? query, int limit)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var term = query.Trim();

        // 优先查服务内存索引：文件变化通过 USN 实时反映，不依赖 .idx compact
        var piped = SearchViaPipe(term, limit);
        if (piped is not null)
            return piped;

        // 回退：服务不可用时直接查 .idx（可能不是最新）
        LiveIndex[] lives;

        lock (_gate)
        {
            lives = _liveIndexes.Values.ToArray();
        }

        if (lives.Length == 0)
            return [];

        var perDrive = new List<FileSearchItem>[lives.Length];

        Parallel.For(0, lives.Length, index =>
        {
            var rows = perDrive[index] = new List<FileSearchItem>(Math.Min(limit, 64));

            try
            {
                IndexV2Searcher.SearchStreaming(lives[index], term, limit, row =>
                {
                    var directory = row.IsDir
                        ? row.Path
                        : Path.GetDirectoryName(row.Path)
                          ?? row.Drive.TrimEnd(':') + ":\\";

                    rows.Add(new FileSearchItem(
                        row.Name,
                        directory,
                        row.Path,
                        row.IsDir,
                        row.Metadata.Size,
                        row.Metadata.Modified));
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Error($"MFT 查询失败：{term}", ex);
            }
        });

        return perDrive
            .Where(rows => rows is not null)
            .SelectMany(rows => rows)
            .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item =>
                item.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Name.Length)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    /// <summary>
    /// 通过命名管道查服务内存里的实时索引。
    /// 返回 null 表示服务不可用（调用方回退到 .idx）；空列表表示服务可用但没有匹配。
    /// </summary>
    private IReadOnlyList<FileSearchItem>? SearchViaPipe(string term, int limit)
    {
        try
        {
            var pipeClient = new SearchPipeClient();

            if (!pipeClient.PingAsync(CancellationToken.None).GetAwaiter().GetResult())
                return null;

            var results = new List<FileSearchItem>(Math.Min(limit, 64));

            var message = new SearchRequestMessage
            {
                Id = SearchRequestId.Search,
                Limit = limit,
                AppLimit = 0,
                Query = term,
                DirectoryFilter = null,
                ExactMatch = false,
                OrFirstPrecedence = false
            };

            SearchPipeClient.SendSearchPipeCommandAsync(
                message,
                row =>
                {
                    var directory = row.IsDir
                        ? row.Path
                        : Path.GetDirectoryName(row.Path)
                          ?? row.Drive.TrimEnd(':') + ":\\";

                    results.Add(new FileSearchItem(
                        row.Name,
                        directory,
                        row.Path,
                        row.IsDir,
                        row.Metadata.Size,
                        row.Metadata.Modified));
                },
                CancellationToken.None).GetAwaiter().GetResult();

            return results
                .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item =>
                    item.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.Name.Length)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;

        lock (_gate)
        {
            foreach (var live in _liveIndexes.Values)
                live.Dispose();

            _liveIndexes.Clear();
        }

        _count = 0;
        _ready = false;
    }

    internal static IReadOnlyList<string> DiscoverDrives()
    {
        try
        {
            var candidates = DriveInfo.GetDrives()
                .Where(drive =>
                    drive.DriveType == DriveType.Fixed
                    && drive.IsReady
                    && !string.IsNullOrWhiteSpace(drive.DriveFormat))
                .Select(drive => drive.Name.TrimEnd('\\', ':', '/'))
                .ToArray();

            var seenSerials = new HashSet<uint>();
            var result = new List<string>(candidates.Length);

            foreach (var drive in candidates)
            {
                if (GetVolumeInformation(
                        drive + ":\\",
                        null,
                        0,
                        out var serial,
                        out _,
                        out _,
                        null,
                        0)
                    && !seenSerials.Add(serial))
                {
                    continue;
                }

                result.Add(drive);
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> NormalizeDrives(IEnumerable<string> drives) =>
        drives
            .Select(drive => drive.TrimEnd('\\', ':', '/'))
            .Where(drive => drive.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        System.Text.StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        System.Text.StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}
