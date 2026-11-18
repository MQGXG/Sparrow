using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 剪贴板历史的落盘实现。
///
/// 文本 / 文件条目直接存进 JSONL；图片统一转成 PNG 存到
/// <c>clipboard-images/</c> 子目录，JSONL 里只记文件名——
/// 大图不进 JSON，历史文件才不会膨胀到打不开。
/// 同一内容连续出现会合并成一条（时间戳刷新），和主流剪贴板管理器一致。
/// </summary>
public sealed class ClipboardHistoryStore : IClipboardHistory
{
    private const int DefaultMaxEntries = 200;

    /// <summary>超过 32MB 的图片不记录：占盘且几乎不会还原使用。</summary>
    private const long MaxImageBytes = 32 * 1024 * 1024;

    /// <summary>超过 256K 字符的文本只记摘要，不记全文（与 TextInput 截断阈值一致）。</summary>
    private const int MaxTextChars = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly string _imagesDirectory;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    private int _maxEntries;
    private DateTimeOffset _lastTimestamp;
    private List<StoredEntry> _entries = [];

    public ClipboardHistoryStore(
        string path,
        IAppLog log,
        int maxEntries = DefaultMaxEntries)
    {
        _path = path;
        _imagesDirectory = Path.Combine(
            Path.GetDirectoryName(path) ?? ".",
            "clipboard-images");
        _log = log;
        _maxEntries = Math.Max(1, maxEntries);

        Load();
        TryTrim();
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>记录一段文本。与历史中的重复内容合并并刷新时间戳；收藏与标签跟着保留。</summary>
    public void AddText(string text, string? sourceApp = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var full = text.Length > MaxTextChars ? null : text;
        var hash = HashText(text);
        var autoTags = ClipboardClassifier.ClassifyText(text).ToArray();

        lock (_gate)
        {
            var previous = TakeDuplicate(ClipboardEntryKind.Text, hash);

            Insert(new StoredEntry
            {
                Id = NewId(),
                Kind = ClipboardEntryKind.Text,
                Preview = BuildPreview(text),
                Text = full,
                ContentHash = hash,
                ByteSize = text.Length,
                CreatedAt = NextTimestamp(),
                AutoTags = autoTags,
                Favorite = previous?.Favorite ?? false,
                Tags = previous?.Tags ?? [],
                SourceApp = sourceApp
            });
        }

        Changed?.Invoke();
    }

    /// <summary>记录一张剪贴板图片（统一 PNG）。重复图片同样合并。</summary>
    public void AddImage(byte[] png, string? sourceApp = null)
    {
        if (png is not { Length: > 0 })
            return;

        if (png.Length > MaxImageBytes)
        {
            _log.Info($"剪贴板图片 {png.Length} 字节，超过记录上限，跳过");
            return;
        }

        var hash = HashBytes(png);
        var inserted = false;

        lock (_gate)
        {
            var previous = TakeDuplicate(ClipboardEntryKind.Image, hash);

            var id = NewId();
            var file = $"{id}.png";

            try
            {
                Directory.CreateDirectory(_imagesDirectory);
                File.WriteAllBytes(Path.Combine(_imagesDirectory, file), png);
            }
            catch (Exception ex)
            {
                // 图片写不进去就退化为不记录，绝不让监听线程挂掉
                _log.Warn($"写入剪贴板图片失败：{ex.Message}");
                return;
            }

            Insert(new StoredEntry
            {
                Id = id,
                Kind = ClipboardEntryKind.Image,
                Preview = $"图片 {png.Length / 1024.0:F1} KB",
                ImageFile = file,
                ContentHash = hash,
                ByteSize = png.Length,
                CreatedAt = NextTimestamp(),
                AutoTags = [ClipboardClassifier.TagImage],
                Favorite = previous?.Favorite ?? false,
                Tags = previous?.Tags ?? [],
                SourceApp = sourceApp
            });

            inserted = true;
        }

        if (inserted)
            Changed?.Invoke();
    }

    /// <summary>记录从资源管理器复制的一组文件路径（不复制文件内容）。</summary>
    public void AddFiles(IReadOnlyList<string> paths, string? sourceApp = null)
    {
        if (paths is not { Count: > 0 })
            return;

        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (normalized.Length == 0)
            return;

        var preview = normalized.Length == 1
            ? BuildPreview(normalized[0])
            : $"{normalized.Length} 个文件";

        lock (_gate)
        {
            var filesHash = HashText(string.Join('\n', normalized));
            var previous = TakeDuplicate(ClipboardEntryKind.Files, filesHash);

            Insert(new StoredEntry
            {
                Id = NewId(),
                Kind = ClipboardEntryKind.Files,
                Preview = preview,
                Paths = normalized,
                ContentHash = filesHash,
                ByteSize = normalized.Sum(p => p.Length),
                CreatedAt = NextTimestamp(),
                AutoTags = [.. ClipboardClassifier.ClassifyFiles(normalized)],
                Favorite = previous?.Favorite ?? false,
                Tags = previous?.Tags ?? [],
                SourceApp = sourceApp
            });
        }

        Changed?.Invoke();
    }

    /// <summary>清空全部记录和图片文件。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                _entries = [];
                if (File.Exists(_path))
                    File.Delete(_path);
                if (Directory.Exists(_imagesDirectory))
                    Directory.Delete(_imagesDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _log.Warn($"清空剪贴板历史失败：{ex.Message}");
                return;
            }
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<ClipboardHistoryEntry> Recent(int count)
    {
        lock (_gate)
            return _entries
                .Take(Math.Max(0, count))
                .Select(ToEntry)
                .ToArray();
    }

    public IReadOnlyList<ClipboardHistoryEntry> Search(string keyword, int count)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return Recent(count);

        lock (_gate)
            return _entries
                .Where(entry => Matches(entry, keyword))
                .Take(Math.Max(0, count))
                .Select(ToEntry)
                .ToArray();
    }

    public byte[]? LoadImage(ClipboardHistoryEntry entry)
    {
        string? file;

        lock (_gate)
            file = _entries.FirstOrDefault(e => e.Id == entry.Id)?.ImageFile;

        if (file is null)
            return null;

        try
        {
            return File.ReadAllBytes(Path.Combine(_imagesDirectory, file));
        }
        catch (Exception ex)
        {
            _log.Warn($"读取剪贴板图片失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>保留策略变化时由设置页调用。</summary>
    public void Configure(int maxEntries)
    {
        lock (_gate)
        {
            _maxEntries = Math.Max(1, maxEntries);
            TryTrim();
        }

        Changed?.Invoke();
    }

    /// <summary>历史发生变化（新增 / 收藏 / 标签 / 删除）。监听来自后台线程，订阅方自行切到 UI 线程。</summary>
    public event Action? Changed;

    /// <summary>切换收藏。条目不存在时返回 false。</summary>
    public bool SetFavorite(string id, bool favorite) =>
        Update(id, entry => entry with { Favorite = favorite });

    /// <summary>替换手动标签：去空白、去重、限长限量。</summary>
    public bool SetTags(string id, IReadOnlyList<string> tags)
    {
        var cleaned = NormalizeTags(tags);
        return Update(id, entry => entry with { Tags = cleaned });
    }

    /// <summary>删除单条；图片条目的 PNG 一并删掉。</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0)
                return false;

            var removed = _entries[index];
            _entries.RemoveAt(index);
            DeleteImageFile(removed);
            TrimAndPersist();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 把一条被删除的条目写回历史（撤销删除）。
    ///
    /// 保留收藏 / 标签 / 来源应用；内容指纹重新计算，若已有同内容条目会先合并。
    /// 图片需要调用方把 PNG 字节一起带回来——删除时它已经从磁盘移除。
    /// </summary>
    public bool Restore(ClipboardHistoryEntry entry, byte[]? imagePng = null)
    {
        lock (_gate)
        {
            string? imageFile = null;
            string hash;

            switch (entry.Kind)
            {
                case ClipboardEntryKind.Text when entry.Text is { Length: > 0 }:
                    hash = HashText(entry.Text);
                    break;

                case ClipboardEntryKind.Image when imagePng is { Length: > 0 }:
                    var id = NewId();
                    imageFile = $"{id}.png";

                    try
                    {
                        Directory.CreateDirectory(_imagesDirectory);
                        File.WriteAllBytes(Path.Combine(_imagesDirectory, imageFile), imagePng);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"撤销删除时写入图片失败：{ex.Message}");
                        return false;
                    }

                    hash = HashBytes(imagePng);
                    break;

                case ClipboardEntryKind.Files when entry.Paths.Count > 0:
                    hash = HashText(string.Join('\n', entry.Paths));
                    break;

                default:
                    return false;
            }

            TakeDuplicate(entry.Kind, hash);

            Insert(new StoredEntry
            {
                Id = NewId(),
                Kind = entry.Kind,
                Preview = entry.Preview,
                Text = entry.Text,
                ImageFile = imageFile,
                Paths = entry.Paths.ToArray(),
                ContentHash = hash,
                ByteSize = entry.Kind == ClipboardEntryKind.Image
                    ? imagePng!.Length
                    : entry.ByteSize,
                CreatedAt = NextTimestamp(),
                AutoTags = ComputeAutoTags(new StoredEntry
                {
                    Kind = entry.Kind,
                    Text = entry.Text,
                    Paths = entry.Paths.ToArray(),
                    Preview = entry.Preview
                }).ToArray(),
                Favorite = entry.Favorite,
                Tags = entry.Tags.ToArray(),
                SourceApp = entry.SourceApp
            });
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>出现过的手动标签，按名称排序；用于筛选条。</summary>
    public IReadOnlyList<string> AllTags()
    {
        lock (_gate)
        {
            return _entries
                .SelectMany(entry => entry.Tags)
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>出现过的来源应用名，按名称排序；用于筛选。</summary>
    public IReadOnlyList<string> AllSourceApps()
    {
        lock (_gate)
        {
            return _entries
                .Select(entry => entry.SourceApp)
                .Where(app => !string.IsNullOrWhiteSpace(app))
                .Select(app => app!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(app => app, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private bool Update(string id, Func<StoredEntry, StoredEntry> update)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0)
                return false;

            _entries[index] = update(_entries[index]);
            TrimAndPersist();
        }

        Changed?.Invoke();
        return true;
    }

    private static string[] NormalizeTags(IReadOnlyList<string> tags)
    {
        var result = new List<string>(tags.Count);

        foreach (var raw in tags)
        {
            var tag = raw.Trim();

            if (tag.Length == 0)
                continue;

            if (tag.Length > 24)
                tag = tag[..24];

            if (!result.Contains(tag, StringComparer.OrdinalIgnoreCase))
                result.Add(tag);

            if (result.Count >= 10)
                break;
        }

        return [.. result];
    }

    private void DeleteImageFile(StoredEntry entry)
    {
        if (entry.ImageFile is null)
            return;

        try
        {
            File.Delete(Path.Combine(_imagesDirectory, entry.ImageFile));
        }
        catch (Exception ex)
        {
            _log.Warn($"删除剪贴板图片失败：{ex.Message}");
        }
    }

    // ---- 内部实现 ----

    private sealed record StoredEntry
    {
        public string Id { get; init; } = string.Empty;
        public ClipboardEntryKind Kind { get; init; }
        public string Preview { get; init; } = string.Empty;

        /// <summary>文本条目的全文；超长文本为 null（只保留摘要，不能还原）。</summary>
        public string? Text { get; init; }

        /// <summary>图片条目在 clipboard-images/ 下的文件名。</summary>
        public string? ImageFile { get; init; }

        public string[] Paths { get; init; } = [];

        /// <summary>内容指纹，用来合并连续重复。</summary>
        public string ContentHash { get; init; } = string.Empty;

        public long ByteSize { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>收藏；不受容量上限清理。</summary>
        public bool Favorite { get; init; }

        /// <summary>用户手动标签。</summary>
        public string[] Tags { get; init; } = [];

        /// <summary>入库时自动识别的标签，老文件缺这个字段时为 null。</summary>
        public string[]? AutoTags { get; init; }

        /// <summary>复制来源进程名；老文件缺这个字段时为空。</summary>
        public string? SourceApp { get; init; }
    }

    /// <summary>
    /// 移除历史中与本次内容相同的旧条目（同形态 + 同指纹），返回它以便继承收藏与标签。
    /// 被移除条目的图片文件一并删掉，不留孤儿文件。
    /// 调用方随后照常插入新条目，实现「删旧插新、时间戳刷新、置顶」。
    /// </summary>
    private StoredEntry? TakeDuplicate(ClipboardEntryKind kind, string hash)
    {
        var index = _entries.FindIndex(entry =>
            entry.Kind == kind
            && string.Equals(entry.ContentHash, hash, StringComparison.Ordinal));

        if (index < 0)
            return null;

        var removed = _entries[index];
        _entries.RemoveAt(index);
        DeleteImageFile(removed);
        return removed;
    }

    /// <summary>
    /// 系统时钟的分辨率是毫秒级，同毫秒内的多次复制会拿到相同时间；
    /// 时间戳还承担「排序」职责，所以强制严格递增。
    /// </summary>
    private DateTimeOffset NextTimestamp()
    {
        var now = DateTimeOffset.Now;
        if (now <= _lastTimestamp)
            now = _lastTimestamp.AddMilliseconds(1);

        _lastTimestamp = now;
        return now;
    }

    private void Insert(StoredEntry entry)
    {
        _entries.Insert(0, entry);
        TrimAndPersist();
    }

    private static ClipboardHistoryEntry ToEntry(StoredEntry entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind,
        Preview = entry.Preview,
        Text = entry.Text,
        Paths = entry.Paths,
        CreatedAt = entry.CreatedAt,
        ByteSize = entry.ByteSize,
        Favorite = entry.Favorite,
        Tags = entry.Tags,
        // 老文件没有这个字段：按当前规则补算，避免升级后旧条目全都没有分类
        AutoTags = entry.AutoTags ?? ComputeAutoTags(entry),
        SourceApp = entry.SourceApp
    };

    private static IReadOnlyList<string> ComputeAutoTags(StoredEntry entry) => entry.Kind switch
    {
        ClipboardEntryKind.Text when entry.Text is not null => ClipboardClassifier.ClassifyText(entry.Text),
        ClipboardEntryKind.Text => ClipboardClassifier.ClassifyText(entry.Preview),
        ClipboardEntryKind.Image => ClipboardClassifier.ClassifyImage(),
        ClipboardEntryKind.Files => ClipboardClassifier.ClassifyFiles(entry.Paths),
        _ => []
    };

    private static bool Matches(StoredEntry entry, string keyword)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;

        return (entry.Text?.Contains(keyword, comparison) ?? false)
            || entry.Preview.Contains(keyword, comparison)
            || entry.Paths.Any(path => path.Contains(keyword, comparison))
            || entry.Tags.Any(tag => tag.Contains(keyword, comparison))
            || (entry.AutoTags?.Any(tag => tag.Contains(keyword, comparison)) ?? false)
            || (entry.SourceApp?.Contains(keyword, comparison) ?? false);
    }

    private static string BuildPreview(string text)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= 80 ? single : single[..80] + "…";
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string HashText(string text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(MD5.HashData(bytes));

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        var entries = new List<StoredEntry>();

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var entry = JsonSerializer.Deserialize<StoredEntry>(line, JsonOptions);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // 损坏的行跳过，不让一条坏记录拖垮整个历史
            }
        }

        _entries = entries;
    }

    /// <summary>
    /// 落盘当前列表；超出上限时先裁剪，被裁掉的图片条目把文件也删掉，
    /// 避免图片目录越积越大。
    ///
    /// 收藏条目不计入上限、也不会被裁掉——用户明确标记「要留下」的内容
    /// 不该因为复制得多就被清走。
    /// </summary>
    private void TrimAndPersist()
    {
        try
        {
            var removed = new List<StoredEntry>();

            if (_entries.Count > _maxEntries)
            {
                var kept = new List<StoredEntry>(_entries.Count);
                var nonFavorites = 0;

                foreach (var entry in _entries)
                {
                    if (entry.Favorite)
                    {
                        kept.Add(entry);
                        continue;
                    }

                    if (nonFavorites < _maxEntries)
                    {
                        kept.Add(entry);
                        nonFavorites++;
                    }
                    else
                    {
                        removed.Add(entry);
                    }
                }

                _entries = kept;
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (_entries.Count == 0)
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            else
            {
                File.WriteAllLines(_path, _entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)));
            }

            foreach (var entry in removed.Where(entry => entry.ImageFile is not null))
            {
                try
                {
                    File.Delete(Path.Combine(_imagesDirectory, entry.ImageFile!));
                }
                catch (Exception ex)
                {
                    _log.Warn($"删除过期剪贴板图片失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // 历史写失败不影响剪贴板使用
            _log.Warn($"写入剪贴板历史失败：{ex.Message}");
        }
    }

    private void TryTrim()
    {
        try
        {
            TrimAndPersist();
        }
        catch (Exception ex)
        {
            _log.Warn($"清理剪贴板历史失败：{ex.Message}");
        }
    }
}
