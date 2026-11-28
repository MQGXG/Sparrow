using System.Text.Json;
using Sparrow.App.Services;

namespace Sparrow.App.History;

/// <summary>
/// JSONL 历史存储：一行一条记录。
///
/// 优点：零依赖、崩溃时最多丢最后一行、可以直接用记事本看。
/// 缺点：查询要全量读。M1 的记录量（几百条）完全够用。
/// </summary>
public sealed class JsonlHistoryStore : IHistoryStore
{
    private const int DefaultMaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    private int _maxEntries;
    private int _retentionDays;

    /// <summary>
    /// 当前有效行数。默认不保留过期数据时，Add 只需比较计数；
    /// 不再每写一条都完整读取 + 反序列化整个文件。
    /// </summary>
    private int _liveCount;

    public JsonlHistoryStore(string path, IAppLog log, int maxEntries = DefaultMaxEntries, int retentionDays = 0)
    {
        _path = path;
        _log = log;
        _maxEntries = Math.Max(1, maxEntries);
        _retentionDays = Math.Max(0, retentionDays);

        // 启动时先按保留策略清一次，避免过期记录一直躺在文件里
        TryTrim();
    }

    /// <summary>更新保留策略并立即清理一次；设置页改完就调用。</summary>
    public void Configure(int maxEntries, int retentionDays)
    {
        lock (_gate)
        {
            _maxEntries = Math.Max(1, maxEntries);
            _retentionDays = Math.Max(0, retentionDays);
            TryTrim();
        }
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            try
            {
                // 结果截断是最后一道闸：插件/历史接口都可能有别的调用方，
                // 落盘前再收一次，保证单条记录不会把 JSONL 撑爆。
                var output = ToolResultLimits.TruncateText(
                    entry.Output,
                    ToolResultLimits.MaxTextChars,
                    out var truncated);

                if (truncated)
                    entry = entry with { Output = output };

                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
                _liveCount++;

                // 没有保留天数时，只有超过条数上限才需要整理；
                // 有保留天数时仍按原逻辑每条都检查一次，保证过期记录及时清掉。
                if (_retentionDays > 0 || _liveCount > _maxEntries)
                    TryTrim();
            }
            catch (Exception ex)
            {
                // 历史写失败不影响工具执行
                _log.Warn($"写入历史失败：{ex.Message}");
            }
        }
    }

    public IReadOnlyList<HistoryEntry> Recent(int count)
    {
        lock (_gate)
        {
            var entries = ReadAll();
            return entries
                .TakeLast(Math.Max(0, count))
                .Reverse()
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);

                _liveCount = 0;
            }
            catch (Exception ex)
            {
                _log.Warn($"清空历史失败：{ex.Message}");
            }
        }
    }

    private List<HistoryEntry> ReadAll()
    {
        if (!File.Exists(_path))
            return [];

        var entries = new List<HistoryEntry>();

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // 损坏的行跳过，不让一条坏记录拖垮整个历史
            }
        }

        return entries;
    }

    /// <summary>清理失败不影响主流程：历史只是辅助数据。</summary>
    private void TryTrim()
    {
        try
        {
            TrimIfNeeded();
        }
        catch (Exception ex)
        {
            _log.Warn($"清理历史失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按保留策略清理：先按天数过滤，再按条数截断。
    /// 只有真的删了记录才重写文件，避免每次 Add 都做无谓的 IO。
    /// </summary>
    private void TrimIfNeeded()
    {
        var entries = ReadAll();
        _liveCount = entries.Count;

        if (entries.Count == 0)
            return;

        var kept = entries.AsEnumerable();

        if (_retentionDays > 0)
        {
            var cutoff = DateTimeOffset.Now.AddDays(-_retentionDays);
            kept = kept.Where(entry => entry.CreatedAt >= cutoff);
        }

        var trimmed = kept.TakeLast(_maxEntries).ToArray();
        if (trimmed.Length == entries.Count)
            return;

        if (trimmed.Length == 0)
        {
            File.Delete(_path);
            _liveCount = 0;
            return;
        }

        File.WriteAllLines(_path, trimmed.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));
        _liveCount = trimmed.Length;
    }
}
