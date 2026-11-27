using System.Diagnostics;
using Lertaro.Core.IndexV2;
using Lertaro.Core.IndexV2.Persistence;
using Lertaro.Core.IndexV2.Search;

namespace Sparrow.App.Services;

/// <summary>
/// 基于 Lertaro Core 的 MFT 文件索引。
///
/// 首次使用时如果没有 snapshot，会以管理员权限重新启动 Sparrow.App 自身
/// （带 --build-index 参数），由提权实例扫描 $MFT 并写出列式 snapshot；
/// 普通权限进程只负责 mmap 打开和查询。
///
/// 这条路径的目标是 Everything 级体验：扫描一次约 5–10 秒，
/// 之后查询走内存映射的唯一文件名表，毫秒级返回。
/// </summary>
internal sealed class MftFileIndex : IFileIndex
{
    private readonly IAppLog _log;
    private readonly string _drive;
    private readonly string _snapshotPath;

    private LiveIndex? _live;
    private int _count;
    private volatile bool _started;
    private volatile bool _indexing;
    private volatile bool _ready;
    private volatile string? _statusMessage;

    public MftFileIndex(IAppLog log, string drive = "C")
    {
        _log = log;
        _drive = drive.TrimEnd(':', '\\');
        _snapshotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "index",
            $"{_drive}.snapshot");
    }

    public bool IsReady => _ready;

    public bool IsIndexing => _indexing;

    public int Count => _count;

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
            if (File.Exists(_snapshotPath) && new FileInfo(_snapshotPath).Length > 0)
            {
                Load();
                return;
            }

            _indexing = true;
            _statusMessage = "正在建立文件索引（需要管理员权限）…";
            Changed?.Invoke(this, EventArgs.Empty);

            var built = await RunBuilderAsync();

            if (!built)
            {
                _indexing = false;
                _statusMessage = "索引建立失败；可以稍后重试，或暂时使用简单搜索";
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            Load();
        }
        catch (Exception ex)
        {
            _indexing = false;
            _statusMessage = $"索引初始化失败：{ex.Message}";
            _log.Error("MFT 索引初始化失败", ex);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Load()
    {
        var snapshot = Snapshot.Open(_snapshotPath);
        _live = new LiveIndex(snapshot);
        _count = snapshot.Count;
        _ready = true;
        _indexing = false;
        _statusMessage = null;

        _log.Info($"MFT 索引已加载：{_count:N0} 条");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>以管理员权限启动自身，扫描 $MFT 并写 snapshot。</summary>
    private async Task<bool> RunBuilderAsync()
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
            return false;

        var directory = Path.GetDirectoryName(_snapshotPath);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var statusPath = _snapshotPath + ".status";

        try
        {
            File.Delete(statusPath);
        }
        catch
        {
            // 状态文件删不掉不影响构建
        }

        var arguments = $"--build-index {_drive} --index-output \"{_snapshotPath}\"";

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                Verb = "runas",             // 触发 UAC
                UseShellExecute = true,
                CreateNoWindow = true
            });

            if (process is null)
                return false;

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _log.Warn($"索引构建进程退出码 {process.ExitCode}");
                return false;
            }

            return File.Exists(_snapshotPath)
                   && new FileInfo(_snapshotPath).Length > 0;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = 用户在 UAC 里点了“否”
            _log.Warn($"索引构建未启动：{ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("启动索引构建进程失败", ex);
            return false;
        }
    }

    public IReadOnlyList<FileSearchItem> Search(string? query, int limit)
    {
        var live = _live;

        if (live is null || limit <= 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var results = new List<FileSearchItem>(Math.Min(limit, 64));

        try
        {
            IndexV2Searcher.SearchStreaming(live, query.Trim(), limit, row =>
            {
                var directory = row.IsDir
                    ? row.Path
                    : Path.GetDirectoryName(row.Path)
                      ?? row.Drive.TrimEnd(':') + ":\\";

                results.Add(new FileSearchItem(row.Name, directory, row.Path, row.IsDir));
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Error($"MFT 查询失败：{query}", ex);
        }

        return results;
    }

    public void Dispose()
    {
        _live?.Dispose();
        _live = null;
        _ready = false;
    }
}
