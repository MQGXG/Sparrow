using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Lertaro.Core.IndexV2;
using Lertaro.Core.IndexV2.Persistence;
using Lertaro.Core.IndexV2.Search;

namespace Sparrow.App.Services;

/// <summary>
/// 基于 Lertaro Core 的 MFT 文件索引，支持多个固定盘。
///
/// 每个盘一个 snapshot 文件；首次缺少索引时，以管理员权限重新启动
/// Sparrow.App 自身（--build-index），一次扫描所有缺失盘并写出列式 snapshot。
/// 普通权限进程只负责 mmap 打开和查询。
/// </summary>
internal sealed class MftFileIndex : IFileIndex
{
    private readonly IAppLog _log;
    private readonly IReadOnlyList<string> _drives;
    private readonly string _indexDirectory;

    private readonly Dictionary<string, LiveIndex> _liveIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    private int _count;
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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "index");
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
        _ = Task.Run(BuildOrLoadAsync);
    }

    private async Task BuildOrLoadAsync()
    {
        try
        {
            _indexing = true;

            var missing = new List<string>();

            foreach (var drive in _drives)
            {
                var path = SnapshotPath(drive);

                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    TryLoadDrive(drive, path);
                }
                else
                {
                    missing.Add(drive);
                }
            }

            if (missing.Count == 0)
            {
                FinishBuild();
                return;
            }

            _statusMessage = $"正在建立索引：{string.Join("、", missing)}…";
            Changed?.Invoke(this, EventArgs.Empty);

            await RunBuilderAsync(missing);

            foreach (var drive in missing)
            {
                var path = SnapshotPath(drive);

                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    TryLoadDrive(drive, path);
                else
                    _log.Warn($"{drive} 盘索引缺失或构建失败");
            }

            FinishBuild();
        }
        catch (Exception ex)
        {
            _indexing = false;
            _statusMessage = $"索引初始化失败：{ex.Message}";
            _log.Error("MFT 索引初始化失败", ex);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FinishBuild()
    {
        _indexing = false;
        _ready = Count > 0;
        _statusMessage = _ready ? null : "索引建立失败；可以稍后重试";

        _log.Info($"MFT 索引已加载：{Count:N0} 条 / {_liveIndexes.Count} 个盘");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TryLoadDrive(string drive, string path)
    {
        try
        {
            var snapshot = Snapshot.Open(path);
            var live = new LiveIndex(snapshot);

            lock (_gate)
            {
                if (_liveIndexes.Remove(drive, out var old))
                    old.Dispose();

                _liveIndexes[drive] = live;
            }

            Interlocked.Add(ref _count, snapshot.Count);
        }
        catch (Exception ex)
        {
            _log.Error($"加载 {drive} 盘索引失败：{path}", ex);
        }
    }

    /// <summary>以管理员权限启动自身，扫描所有缺失盘并写 snapshot。</summary>
    private async Task RunBuilderAsync(IReadOnlyList<string> drives)
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
            return;

        Directory.CreateDirectory(_indexDirectory);

        var arguments =
            $"--build-index \"{string.Join(",", drives)}\" --index-directory \"{_indexDirectory}\"";

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                Verb = "runas",             // 触发 UAC
                UseShellExecute = true,
                CreateNoWindow = true
            });

            if (process is null)
                return;

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                _log.Warn($"索引构建进程退出码 {process.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = 用户在 UAC 里点了“否”
            _log.Warn($"索引构建未启动：{ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("启动索引构建进程失败", ex);
        }
    }

    public IReadOnlyList<FileSearchItem> Search(string? query, int limit)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
            return [];

        LiveIndex[] lives;

        lock (_gate)
        {
            lives = _liveIndexes.Values.ToArray();
        }

        if (lives.Length == 0)
            return [];

        var term = query.Trim();
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

                    rows.Add(new FileSearchItem(row.Name, directory, row.Path, row.IsDir));
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

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var live in _liveIndexes.Values)
                live.Dispose();

            _liveIndexes.Clear();
        }

        _count = 0;
        _ready = false;
    }

    /// <summary>
    /// 删除现有 snapshot 并重新走一次「缺失盘 → 提权构建 → 加载」。
    /// 盘列表仍是实例创建时的列表；修改索引盘需要重启宿主。
    /// </summary>
    public void Rebuild()
    {
        lock (_gate)
        {
            foreach (var live in _liveIndexes.Values)
                live.Dispose();

            _liveIndexes.Clear();
        }

        _count = 0;
        _ready = false;
        _indexing = false;
        _started = false;
        _statusMessage = null;

        foreach (var drive in _drives)
        {
            var path = SnapshotPath(drive);

            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".status"); } catch { }
        }

        try { File.Delete(Path.Combine(_indexDirectory, "build.status")); } catch { }

        Start();
    }

    private string SnapshotPath(string drive) =>
        Path.Combine(_indexDirectory, $"{drive}.snapshot");

    private static IReadOnlyList<string> NormalizeDrives(IEnumerable<string> drives) =>
        drives
            .Select(drive => drive.TrimEnd('\\', ':', '/'))
            .Where(drive => drive.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

            // subst / 同一卷的多个盘符：卷序列号相同，只索引第一个，避免重复扫描
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}
