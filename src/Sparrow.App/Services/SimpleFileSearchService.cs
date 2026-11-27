namespace Sparrow.App.Services;

/// <summary>一条文件搜索结果。</summary>
public sealed record FileSearchItem(
    string Name,
    string DirectoryPath,
    string FullPath,
    bool IsDirectory,
    long Size = 0,
    DateTime Modified = default);

/// <summary>
/// 最小可用的本机文件索引。
///
/// 现在先用最朴素的方式把固定盘的文件名收进内存，搜索时线性过滤；
/// 它解决「双击 Ctrl 能搜到文件」这个闭环，后续可以整体替换成
/// MFT + USN 索引，而不影响窗口和交互层。
///
/// 索引在后台线程构建，完成前 <see cref="IsReady"/> 为 false。
/// 结果只保存名字和路径，不读文件内容。
/// </summary>
public sealed class SimpleFileSearchService : IFileIndex
{
    /// <summary>内存上限：再多就不是「简单搜索」了，先截断避免吃光内存。</summary>
    private const int DefaultMaxEntries = 300_000;

    /// <summary>这些目录里的东西不参与索引：系统目录和常见构建产物。</summary>
    private static readonly string[] ExcludedDirectoryNames =
    [
        "$Recycle.Bin",
        "System Volume Information",
        "$WinREAgent",
        "Recovery",
        "PerfLogs",
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "AppData",
        "node_modules",
        ".git",
        ".vs",
        ".nuget",
        "obj",
        "bin",
        "packages"
    ];

    private readonly IAppLog _log;
    private readonly IReadOnlyList<string> _roots;
    private readonly int _maxEntries;
    private readonly CancellationTokenSource _cts = new();

    private FileSearchItem[] _items = [];
    private volatile bool _indexing;
    private volatile bool _ready;
    private volatile int _count;

    public SimpleFileSearchService(
        IAppLog log,
        IEnumerable<string>? roots = null,
        int maxEntries = DefaultMaxEntries)
    {
        _log = log;
        _roots = (roots ?? DefaultRoots()).ToArray();
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>索引是否已经建完。</summary>
    public bool IsReady => _ready;

    /// <summary>是否正在建索引。</summary>
    public bool IsIndexing => _indexing;

    /// <summary>当前索引里的条目数。</summary>
    public int Count => _count;

    /// <summary>索引完成时触发；UI 用它刷新一次结果。</summary>
    public event EventHandler? Changed;

    /// <summary>后台开始建索引；重复调用是空操作。</summary>
    public void Start()
    {
        if (_indexing || _ready)
            return;

        _indexing = true;

        _ = Task.Run(() =>
        {
            try
            {
                BuildIndex(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 退出时的正常取消
            }
            catch (Exception ex)
            {
                _log.Error("建立文件索引失败", ex);
                _indexing = false;
                _ready = true;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    /// <summary>
    /// 同步构建索引；测试可以直接调用，生产走 <see cref="Start"/>。
    /// </summary>
    internal void BuildIndex(CancellationToken token)
    {
        var results = new List<FileSearchItem>(capacity: 4096);

        // 每个盘一个队列，轮询处理：避免某个大盘（通常是 C:）先吃满上限，
        // 其它盘一条都没索引。
        var queues = new List<Queue<string>>(_roots.Count);

        foreach (var root in _roots)
        {
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                queues.Add(new Queue<string>([root]));
        }

        while (queues.Count > 0 && results.Count < _maxEntries && !token.IsCancellationRequested)
        {
            var progressed = false;

            foreach (var queue in queues)
            {
                if (queue.Count == 0 || results.Count >= _maxEntries || token.IsCancellationRequested)
                    continue;

                var directory = queue.Dequeue();
                progressed = true;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        if (results.Count >= _maxEntries)
                            break;

                        results.Add(new FileSearchItem(
                            Path.GetFileName(file),
                            directory,
                            file,
                            IsDirectory: false));
                    }

                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        if (results.Count >= _maxEntries || token.IsCancellationRequested)
                            break;

                        if (ShouldSkip(child))
                            continue;

                        // junction / 符号链接可能指回上层目录，跳过避免死循环
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                            continue;

                        // 目录本身也进索引，否则只能搜到文件、搜不到文件夹
                        results.Add(new FileSearchItem(
                            Path.GetFileName(child),
                            directory,
                            child,
                            IsDirectory: true));

                        queue.Enqueue(child);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // 没权限的目录直接跳过，不中断整盘
                }
                catch (IOException)
                {
                    // 目录在枚举时被删/改名，跳过
                }
                catch (Exception ex)
                {
                    _log.Warn($"枚举目录失败，已跳过：{directory}（{ex.Message}）");
                }

                // 构建过程中也让状态栏的数字动起来
                _count = results.Count;
            }

            if (!progressed)
                break;
        }

        token.ThrowIfCancellationRequested();

        _items = results.ToArray();
        _count = _items.Length;
        _indexing = false;
        _ready = true;

        _log.Info($"文件索引完成：{_count} 条");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 在内存索引里按文件名过滤：前缀命中优先，其次包含命中。
    /// 空查询返回空列表，避免窗口一打开就铺满内容。
    /// </summary>
    public IReadOnlyList<FileSearchItem> Search(string? query, int limit)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var items = _items;
        if (items.Length == 0)
            return [];

        var term = query.Trim();
        var prefix = new List<FileSearchItem>(Math.Min(limit, 32));
        var contains = new List<FileSearchItem>(Math.Min(limit, 32));

        foreach (var item in items)
        {
            if (item.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                prefix.Add(item);

                if (prefix.Count >= limit)
                    break;
            }
            else if (contains.Count < limit
                     && item.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                contains.Add(item);
            }
        }

        if (prefix.Count >= limit)
            return prefix;

        var results = new List<FileSearchItem>(prefix.Count + contains.Count);
        results.AddRange(prefix);
        results.AddRange(contains);

        return results.Count > limit ? results.GetRange(0, limit) : results;
    }

    /// <summary>
    /// 简单索引没有持久化文件，重建就是丢掉当前结果、重新开始枚举。
    /// 当前主要作为 MFT 不可用时的回退/测试实现。
    /// </summary>
    public void Rebuild()
    {
        _items = [];
        _count = 0;
        _ready = false;
        _indexing = false;
        Start();
    }

    private static bool ShouldSkip(string path)
    {
        var name = Path.GetFileName(path);

        foreach (var excluded in ExcludedDirectoryNames)
        {
            if (name.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> DefaultRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        // 只取消，不 Dispose：后台线程可能仍在使用这个 token
        _cts.Cancel();
    }
}
