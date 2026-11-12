using System.Text.Json;

namespace Sparrow.App.History;

/// <summary>
/// JSONL 历史存储：一行一条记录。
///
/// 优点：零依赖、崩溃时最多丢最后一行、可以直接用记事本看。
/// 缺点：查询要全量读。M1 的记录量（几百条）完全够用。
/// </summary>
public sealed class JsonlHistoryStore : IHistoryStore
{
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    public JsonlHistoryStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
                TrimIfNeeded();
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

    /// <summary>超过上限时只保留最近的部分，避免文件无限增长。</summary>
    private void TrimIfNeeded()
    {
        var entries = ReadAll();
        if (entries.Count <= MaxEntries)
            return;

        var kept = entries.TakeLast(MaxEntries).ToArray();
        File.WriteAllLines(_path, kept.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));
    }
}
