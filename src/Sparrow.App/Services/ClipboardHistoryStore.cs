using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 剪贴板历史的 SQLite 实现（WAL + FTS5 trigram）。
///
/// 为什么不用 JSONL：每条记录变更都要整文件重写，搜索只能内存全扫；
/// 数据库给的是行级更新、事务写入和索引搜索。图片仍然放文件，
/// 数据库只存文件名——预览、拖出都直接读文件，备份也直观。
///
/// 线程模型：监听线程写、UI 线程读；所有操作在 <see cref="_gate"/> 内、
/// 每次操作独立连接（连接池），避免跨线程共享连接。
/// </summary>
public sealed class ClipboardHistoryStore : IClipboardHistory
{
    private const int DefaultMaxEntries = 200;

    /// <summary>超过 32MB 的图片不记录：占盘且几乎不会还原使用。</summary>
    private const long MaxImageBytes = 32 * 1024 * 1024;

    /// <summary>超过 256K 字符的文本只记摘要，不记全文（与 TextInput 截断阈值一致）。</summary>
    private const int MaxTextChars = 256 * 1024;

    private const string SelectColumns =
        "id, kind, preview, text, image_file, paths, content_hash, byte_size, created_at, favorite, tags, auto_tags, source_app";

    /// <summary>与 FTS 表 JOIN 时必须带表别名，否则 preview / text 等列名会歧义。</summary>
    private const string SelectColumnsJoined =
        "e.id, e.kind, e.preview, e.text, e.image_file, e.paths, e.content_hash, e.byte_size, e.created_at, e.favorite, e.tags, e.auto_tags, e.source_app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // 中文不要转成 \uXXXX：标签/路径要能直接 LIKE 和肉眼看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly string _imagesDirectory;
    private readonly string _legacyJsonlPath;
    private readonly string _connectionString;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    private readonly bool _ftsEnabled;
    private int _maxEntries;
    private int _retentionDays;
    private long _maxBytes;
    private long _lastTimestampMs;

    public ClipboardHistoryStore(
        string path,
        IAppLog log,
        int maxEntries = DefaultMaxEntries,
        int retentionDays = 0,
        long maxBytes = 0)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);

        _imagesDirectory = Path.Combine(directory, "clipboard-images");
        _legacyJsonlPath = Path.Combine(directory, "clipboard-history.jsonl");
        _log = log;
        _maxEntries = Math.Max(1, maxEntries);
        _retentionDays = Math.Max(0, retentionDays);
        _maxBytes = Math.Max(0, maxBytes);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        var isNewDatabase = !File.Exists(path);

        _ftsEnabled = InitializeDatabase();
        _lastTimestampMs = ReadMaxTimestamp();

        // 只有新建数据库才迁移旧 JSONL，避免「清空历史后重启又被旧数据灌回来」
        if (isNewDatabase)
            MigrateLegacyJsonl();

        _lastTimestampMs = Math.Max(_lastTimestampMs, ReadMaxTimestamp());
        TrimStandalone();
    }

    /// <summary>当前构建是否启用了 FTS5 trigram 索引；不可用时搜索自动退化 LIKE。</summary>
    public bool UsesFullTextIndex => _ftsEnabled;

    public int Count => (int)Scalar("SELECT COUNT(*) FROM entries");

    /// <summary>历史占用的近似字节数：文本按字符数、图片按 PNG 字节数。</summary>
    public long ApproximateBytes => Scalar("SELECT COALESCE(SUM(byte_size), 0) FROM entries");

    /// <summary>历史发生变化（新增 / 收藏 / 标签 / 删除）。监听来自后台线程，订阅方自行切到 UI 线程。</summary>
    public event Action? Changed;

    /// <summary>记录一段文本。与历史中的重复内容合并并刷新时间戳；收藏与标签跟着保留。</summary>
    public void AddText(string text, string? sourceApp = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var full = text.Length > MaxTextChars ? null : text;
        var hash = HashText(text);
        var autoTags = ClipboardClassifier.ClassifyText(text).ToArray();
        var removedImages = new List<string>();

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                var previous = TakeDuplicate(connection, transaction, ClipboardEntryKind.Text, hash, removedImages);

                InsertRow(connection, transaction, new EntryRow(
                    NewId(),
                    ClipboardEntryKind.Text,
                    BuildPreview(text),
                    full,
                    ImageFile: null,
                    [],
                    hash,
                    text.Length,
                    NextTimestamp(),
                    previous?.Favorite ?? false,
                    previous?.Tags ?? [],
                    autoTags,
                    sourceApp));

                Trim(connection, transaction, removedImages);
                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"写入剪贴板历史失败：{ex.Message}");
            return;
        }

        DeleteImageFiles(removedImages);
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
        var removedImages = new List<string>();
        string? writtenFile = null;

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                var previous = TakeDuplicate(connection, transaction, ClipboardEntryKind.Image, hash, removedImages);

                var id = NewId();
                writtenFile = $"{id}.png";

                Directory.CreateDirectory(_imagesDirectory);
                File.WriteAllBytes(Path.Combine(_imagesDirectory, writtenFile), png);

                InsertRow(connection, transaction, new EntryRow(
                    id,
                    ClipboardEntryKind.Image,
                    $"图片 {png.Length / 1024.0:F1} KB",
                    Text: null,
                    writtenFile,
                    [],
                    hash,
                    png.Length,
                    NextTimestamp(),
                    previous?.Favorite ?? false,
                    previous?.Tags ?? [],
                    [ClipboardClassifier.TagImage],
                    sourceApp));

                Trim(connection, transaction, removedImages);
                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            // 图片写不进去就退化为不记录，绝不让监听线程挂掉
            _log.Warn($"写入剪贴板图片失败：{ex.Message}");

            if (writtenFile is not null)
                DeleteImageFiles([writtenFile]);

            return;
        }

        DeleteImageFiles(removedImages);
        Changed?.Invoke();
    }

    /// <summary>记录从资源管理器复制的一组文件路径（不复制文件内容）。</summary>
    public void AddFiles(IReadOnlyList<string> paths, string? sourceApp = null)
    {
        if (paths is not { Count: > 0 })
            return;

        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (normalized.Length == 0)
            return;

        var preview = normalized.Length == 1
            ? BuildPreview(normalized[0])
            : $"{normalized.Length} 个文件";
        var hash = HashText(string.Join('\n', normalized));
        var removedImages = new List<string>();

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                var previous = TakeDuplicate(connection, transaction, ClipboardEntryKind.Files, hash, removedImages);

                InsertRow(connection, transaction, new EntryRow(
                    NewId(),
                    ClipboardEntryKind.Files,
                    preview,
                    Text: null,
                    ImageFile: null,
                    normalized,
                    hash,
                    normalized.Sum(path => (long)path.Length),
                    NextTimestamp(),
                    previous?.Favorite ?? false,
                    previous?.Tags ?? [],
                    [.. ClipboardClassifier.ClassifyFiles(normalized)],
                    sourceApp));

                Trim(connection, transaction, removedImages);
                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"写入剪贴板文件记录失败：{ex.Message}");
            return;
        }

        DeleteImageFiles(removedImages);
        Changed?.Invoke();
    }

    /// <summary>清空全部记录和图片文件。</summary>
    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM entries";
                    command.ExecuteNonQuery();
                }

                transaction.Commit();

                if (Directory.Exists(_imagesDirectory))
                    Directory.Delete(_imagesDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"清空剪贴板历史失败：{ex.Message}");
            return;
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<ClipboardHistoryEntry> Recent(int count)
    {
        if (count <= 0)
            return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM entries ORDER BY created_at DESC LIMIT $count";
        command.Parameters.AddWithValue("$count", count);

        return ReadEntries(command);
    }

    public IReadOnlyList<ClipboardHistoryEntry> Search(string keyword, int count)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return Recent(count);

        if (count <= 0)
            return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        // trigram 分词要求查询至少 3 个字符；短词和中文两字词走 LIKE
        if (_ftsEnabled && keyword.Length >= 3)
        {
            command.CommandText = $"""
                SELECT {SelectColumnsJoined}
                FROM entries_fts f
                JOIN entries e ON e.rowid = f.rowid
                WHERE entries_fts MATCH $query
                ORDER BY e.created_at DESC
                LIMIT $count
                """;
            command.Parameters.AddWithValue("$query", QuoteFts(keyword));
        }
        else
        {
            command.CommandText = $"""
                SELECT {SelectColumns}
                FROM entries
                WHERE text LIKE $like ESCAPE '\'
                   OR preview LIKE $like ESCAPE '\'
                   OR tags LIKE $like ESCAPE '\'
                   OR IFNULL(source_app, '') LIKE $like ESCAPE '\'
                ORDER BY created_at DESC
                LIMIT $count
                """;
            command.Parameters.AddWithValue("$like", $"%{EscapeLike(keyword)}%");
        }

        command.Parameters.AddWithValue("$count", count);
        return ReadEntries(command);
    }

    public byte[]? LoadImage(ClipboardHistoryEntry entry)
    {
        string? file;

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT image_file FROM entries WHERE id = $id";
            command.Parameters.AddWithValue("$id", entry.Id);

            file = command.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取剪贴板图片记录失败：{ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(file))
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

    /// <summary>保留条数变化时调用；立即裁剪一次。</summary>
    public void Configure(int maxEntries)
    {
        lock (_gate)
        {
            _maxEntries = Math.Max(1, maxEntries);
            TrimStandalone();
        }

        Changed?.Invoke();
    }

    /// <summary>保留策略（天数 / 容量）变化时调用；立即按新策略清理一次。</summary>
    public void ConfigureRetention(int retentionDays, long maxBytes)
    {
        lock (_gate)
        {
            _retentionDays = Math.Max(0, retentionDays);
            _maxBytes = Math.Max(0, maxBytes);
            TrimStandalone();
        }

        Changed?.Invoke();
    }

    /// <summary>切换收藏。</summary>
    public bool SetFavorite(string id, bool favorite)
    {
        var changed = Execute(
            "UPDATE entries SET favorite = $favorite WHERE id = $id",
            ("$favorite", favorite ? 1 : 0),
            ("$id", id));

        if (changed)
            Changed?.Invoke();

        return changed;
    }

    /// <summary>替换手动标签：去空白、去重、限长限量。</summary>
    public bool SetTags(string id, IReadOnlyList<string> tags)
    {
        var cleaned = NormalizeTags(tags);
        var changed = Execute(
            "UPDATE entries SET tags = $tags WHERE id = $id",
            ("$tags", Serialize(cleaned)),
            ("$id", id));

        if (changed)
            Changed?.Invoke();

        return changed;
    }

    /// <summary>删除单条；图片条目的 PNG 一并删掉。</summary>
    public bool Delete(string id)
    {
        string? imageFile;

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();

                using (var select = connection.CreateCommand())
                {
                    select.CommandText = "SELECT image_file FROM entries WHERE id = $id";
                    select.Parameters.AddWithValue("$id", id);

                    using var reader = select.ExecuteReader();
                    if (!reader.Read())
                        return false;

                    imageFile = reader.IsDBNull(0) ? null : reader.GetString(0);
                }

                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM entries WHERE id = $id";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"删除剪贴板条目失败：{ex.Message}");
            return false;
        }

        if (imageFile is not null)
            DeleteImageFiles([imageFile]);

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 把一条被删除的条目写回历史（撤销删除）。
    /// 保留收藏 / 标签 / 来源；图片需要调用方把 PNG 字节一起带回来。
    /// </summary>
    public bool Restore(ClipboardHistoryEntry entry, byte[]? imagePng = null)
    {
        string hash;
        string? imageFile = null;
        long byteSize;

        switch (entry.Kind)
        {
            case ClipboardEntryKind.Text when entry.Text is { Length: > 0 }:
                hash = HashText(entry.Text);
                byteSize = entry.Text.Length;
                break;

            case ClipboardEntryKind.Image when imagePng is { Length: > 0 }:
                hash = HashBytes(imagePng);
                byteSize = imagePng.Length;
                break;

            case ClipboardEntryKind.Files when entry.Paths.Count > 0:
                hash = HashText(string.Join('\n', entry.Paths));
                byteSize = entry.Paths.Sum(path => (long)path.Length);
                break;

            default:
                return false;
        }

        var removedImages = new List<string>();

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();

                var previous = TakeDuplicate(connection, transaction, entry.Kind, hash, removedImages);

                if (entry.Kind == ClipboardEntryKind.Image)
                {
                    imageFile = $"{NewId()}.png";

                    try
                    {
                        Directory.CreateDirectory(_imagesDirectory);
                        File.WriteAllBytes(Path.Combine(_imagesDirectory, imageFile), imagePng!);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"撤销删除时写入图片失败：{ex.Message}");
                        transaction.Rollback();
                        return false;
                    }
                }

                InsertRow(connection, transaction, new EntryRow(
                    NewId(),
                    entry.Kind,
                    entry.Preview,
                    entry.Text,
                    imageFile,
                    entry.Paths.ToArray(),
                    hash,
                    byteSize,
                    NextTimestamp(),
                    entry.Favorite,
                    entry.Tags.ToArray(),
                    ComputeAutoTags(entry.Kind, entry.Text, entry.Preview, entry.Paths).ToArray(),
                    entry.SourceApp));

                Trim(connection, transaction, removedImages);
                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"撤销删除失败：{ex.Message}");

            if (imageFile is not null)
                DeleteImageFiles([imageFile]);

            return false;
        }

        DeleteImageFiles(removedImages);
        Changed?.Invoke();
        return true;
    }

    /// <summary>出现过的手动标签，按名称排序。</summary>
    public IReadOnlyList<string> AllTags()
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT tags FROM entries";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                foreach (var tag in Deserialize(reader.GetString(0)))
                    tags.Add(tag);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"读取剪贴板标签失败：{ex.Message}");
            return [];
        }

        return tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>出现过的来源应用名，按名称排序。</summary>
    public IReadOnlyList<string> AllSourceApps()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT source_app
                FROM entries
                WHERE source_app IS NOT NULL AND source_app <> ''
                ORDER BY source_app COLLATE NOCASE
                """;

            var apps = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                apps.Add(reader.GetString(0));

            return apps;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取剪贴板来源失败：{ex.Message}");
            return [];
        }
    }

    // ---- 初始化与迁移 ----

    private bool InitializeDatabase()
    {
        using var connection = OpenConnection();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS entries (
                    id           TEXT PRIMARY KEY,
                    kind         INTEGER NOT NULL,
                    preview      TEXT NOT NULL,
                    text         TEXT,
                    image_file   TEXT,
                    paths        TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    byte_size    INTEGER NOT NULL,
                    created_at   INTEGER NOT NULL,
                    favorite     INTEGER NOT NULL DEFAULT 0,
                    tags         TEXT NOT NULL DEFAULT '[]',
                    auto_tags    TEXT NOT NULL DEFAULT '[]',
                    source_app   TEXT
                );

                CREATE INDEX IF NOT EXISTS ix_entries_created ON entries(created_at DESC);
                CREATE INDEX IF NOT EXISTS ix_entries_hash ON entries(kind, content_hash);
                CREATE INDEX IF NOT EXISTS ix_entries_favorite ON entries(favorite);

                PRAGMA user_version = 1;
                """;
            schema.ExecuteNonQuery();
        }

        return TryCreateFullTextIndex(connection);
    }

    private bool TryCreateFullTextIndex(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                    text,
                    preview,
                    tags,
                    source_app,
                    content = 'entries',
                    content_rowid = 'rowid',
                    tokenize = 'trigram'
                );

                CREATE TRIGGER IF NOT EXISTS entries_ai AFTER INSERT ON entries BEGIN
                    INSERT INTO entries_fts(rowid, text, preview, tags, source_app)
                    VALUES (new.rowid, new.text, new.preview, new.tags, new.source_app);
                END;

                CREATE TRIGGER IF NOT EXISTS entries_ad AFTER DELETE ON entries BEGIN
                    INSERT INTO entries_fts(entries_fts, rowid, text, preview, tags, source_app)
                    VALUES ('delete', old.rowid, old.text, old.preview, old.tags, old.source_app);
                END;

                CREATE TRIGGER IF NOT EXISTS entries_au AFTER UPDATE ON entries BEGIN
                    INSERT INTO entries_fts(entries_fts, rowid, text, preview, tags, source_app)
                    VALUES ('delete', old.rowid, old.text, old.preview, old.tags, old.source_app);
                    INSERT INTO entries_fts(rowid, text, preview, tags, source_app)
                    VALUES (new.rowid, new.text, new.preview, new.tags, new.source_app);
                END;
                """;
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex)
        {
            // 极少数 SQLite 构建没有 trigram 分词器：退化为 LIKE，功能不受影响
            _log.Warn($"剪贴板全文索引不可用，搜索退化为 LIKE：{ex.Message}");
            return false;
        }
    }

    private void MigrateLegacyJsonl()
    {
        if (!File.Exists(_legacyJsonlPath))
            return;

        try
        {
            var rows = new List<EntryRow>();

            foreach (var line in File.ReadLines(_legacyJsonlPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<LegacyEntry>(line, JsonOptions);
                    if (entry is null || string.IsNullOrWhiteSpace(entry.Id))
                        continue;

                    rows.Add(new EntryRow(
                        entry.Id,
                        entry.Kind,
                        entry.Preview,
                        entry.Text,
                        entry.ImageFile,
                        entry.Paths,
                        string.IsNullOrEmpty(entry.ContentHash) ? ComputeHash(entry) : entry.ContentHash,
                        entry.ByteSize,
                        entry.CreatedAt.ToUnixTimeMilliseconds(),
                        entry.Favorite,
                        entry.Tags,
                        entry.AutoTags ?? ComputeAutoTags(entry.Kind, entry.Text, entry.Preview, entry.Paths).ToArray(),
                        entry.SourceApp));
                }
                catch (JsonException)
                {
                    // 损坏的行跳过，不让一条坏记录拖垮整个迁移
                }
            }

            if (rows.Count > 0)
            {
                lock (_gate)
                {
                    using var connection = OpenConnection();
                    using var transaction = connection.BeginTransaction();

                    foreach (var row in rows)
                        InsertRow(connection, transaction, row);

                    transaction.Commit();
                }

                _lastTimestampMs = Math.Max(_lastTimestampMs, rows.Max(row => row.CreatedAtMs));
                _log.Info($"剪贴板历史已从 JSONL 迁移到 SQLite：{rows.Count} 条");
            }

            MoveLegacyFile();
        }
        catch (Exception ex)
        {
            // 迁移失败保留 JSONL，数据库为空；下次启动可重试（删除 clipboard.db）
            _log.Warn($"剪贴板历史迁移失败：{ex.Message}");
        }
    }

    private void MoveLegacyFile()
    {
        try
        {
            var backup = _legacyJsonlPath + ".bak";
            if (File.Exists(backup))
                File.Delete(backup);

            File.Move(_legacyJsonlPath, backup);
        }
        catch (Exception ex)
        {
            _log.Warn($"重命名旧剪贴板历史失败：{ex.Message}");
        }
    }

    private void InsertRow(SqliteConnection connection, SqliteTransaction transaction, EntryRow row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entries (
                id, kind, preview, text, image_file, paths,
                content_hash, byte_size, created_at, favorite, tags, auto_tags, source_app)
            VALUES (
                $id, $kind, $preview, $text, $image, $paths,
                $hash, $size, $created, $favorite, $tags, $auto, $source)
            """;

        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$kind", (int)row.Kind);
        command.Parameters.AddWithValue("$preview", row.Preview);
        command.Parameters.AddWithValue("$text", (object?)row.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("$image", (object?)row.ImageFile ?? DBNull.Value);
        command.Parameters.AddWithValue("$paths", Serialize(row.Paths));
        command.Parameters.AddWithValue("$hash", row.ContentHash);
        command.Parameters.AddWithValue("$size", row.ByteSize);
        command.Parameters.AddWithValue("$created", row.CreatedAtMs);
        command.Parameters.AddWithValue("$favorite", row.Favorite ? 1 : 0);
        command.Parameters.AddWithValue("$tags", Serialize(row.Tags));
        command.Parameters.AddWithValue("$auto", Serialize(row.AutoTags));
        command.Parameters.AddWithValue("$source", (object?)row.SourceApp ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    // ---- 裁剪 ----

    private void TrimStandalone()
    {
        var removedImages = new List<string>();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        Trim(connection, transaction, removedImages);
        transaction.Commit();

        DeleteImageFiles(removedImages);
    }

    /// <summary>
    /// 三重裁剪：天数 → 条数 → 容量。收藏条目不参与任何一项。
    /// 被裁掉的图片文件名收集到 <paramref name="removedImages"/>，提交后再删文件。
    /// </summary>
    private void Trim(SqliteConnection connection, SqliteTransaction transaction, List<string> removedImages)
    {
        if (_retentionDays > 0)
        {
            var cutoff = DateTimeOffset.Now.AddDays(-_retentionDays).ToUnixTimeMilliseconds();

            DeleteEntries(
                connection,
                transaction,
                QueryEntries(connection, transaction,
                    "SELECT id, image_file FROM entries WHERE favorite = 0 AND created_at < $cutoff",
                    ("$cutoff", cutoff)),
                removedImages);
        }

        var nonFavoriteCount = Scalar(connection, transaction, "SELECT COUNT(*) FROM entries WHERE favorite = 0");
        if (nonFavoriteCount > _maxEntries)
        {
            var excess = nonFavoriteCount - _maxEntries;

            DeleteEntries(
                connection,
                transaction,
                QueryEntries(connection, transaction,
                    "SELECT id, image_file FROM entries WHERE favorite = 0 ORDER BY created_at ASC LIMIT $excess",
                    ("$excess", excess)),
                removedImages);
        }

        if (_maxBytes > 0)
        {
            var total = Scalar(connection, transaction, "SELECT COALESCE(SUM(byte_size), 0) FROM entries");
            if (total <= _maxBytes)
                return;

            var victims = new List<(string Id, string? ImageFile)>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT id, image_file, byte_size, favorite FROM entries ORDER BY created_at ASC";

                using var reader = command.ExecuteReader();
                while (reader.Read() && total > _maxBytes)
                {
                    if (reader.GetInt32(3) != 0)
                        continue;

                    total -= reader.GetInt64(2);
                    victims.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }

            DeleteEntries(connection, transaction, victims, removedImages);
        }
    }

    private static List<(string Id, string? ImageFile)> QueryEntries(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        var result = new List<(string, string?)>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
            result.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));

        return result;
    }

    private static void DeleteEntries(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<(string Id, string? ImageFile)> entries,
        List<string> removedImages)
    {
        foreach (var (id, imageFile) in entries)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM entries WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();

            if (imageFile is not null)
                removedImages.Add(imageFile);
        }
    }

    // ---- 去重 / 查询 / 工具 ----

    /// <summary>
    /// 移除同形态 + 同指纹的旧条目并返回它，用来继承收藏与标签。
    /// 图片文件名收集到 <paramref name="removedImages"/>，提交后再删。
    /// </summary>
    private static PreviousEntry? TakeDuplicate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardEntryKind kind,
        string hash,
        List<string> removedImages)
    {
        string? id = null;
        var favorite = false;
        string[] tags = [];
        string? imageFile = null;

        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, favorite, tags, image_file
                FROM entries
                WHERE kind = $kind AND content_hash = $hash
                LIMIT 1
                """;
            select.Parameters.AddWithValue("$kind", (int)kind);
            select.Parameters.AddWithValue("$hash", hash);

            using var reader = select.ExecuteReader();
            if (!reader.Read())
                return null;

            id = reader.GetString(0);
            favorite = reader.GetInt32(1) != 0;
            tags = Deserialize(reader.GetString(2));
            imageFile = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM entries WHERE id = $id";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        if (imageFile is not null)
            removedImages.Add(imageFile);

        return new PreviousEntry(favorite, tags);
    }

    private static IReadOnlyList<ClipboardHistoryEntry> ReadEntries(SqliteCommand command)
    {
        var entries = new List<ClipboardHistoryEntry>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
            entries.Add(ToEntry(reader));

        return entries;
    }

    private static ClipboardHistoryEntry ToEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Kind = (ClipboardEntryKind)reader.GetInt32(1),
        Preview = reader.GetString(2),
        Text = reader.IsDBNull(3) ? null : reader.GetString(3),
        Paths = Deserialize(reader.GetString(5)),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)).ToLocalTime(),
        ByteSize = reader.GetInt64(7),
        Favorite = reader.GetInt32(9) != 0,
        Tags = Deserialize(reader.GetString(10)),
        AutoTags = Deserialize(reader.GetString(11)),
        SourceApp = reader.IsDBNull(12) ? null : reader.GetString(12)
    };

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000; PRAGMA synchronous = NORMAL;";
        command.ExecuteNonQuery();

        return connection;
    }

    private long Scalar(string sql)
    {
        using var connection = OpenConnection();
        return Scalar(connection, null, sql);
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private bool Execute(string sql, params (string Name, object Value)[] parameters)
    {
        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;

                foreach (var (name, value) in parameters)
                    command.Parameters.AddWithValue(name, value);

                return command.ExecuteNonQuery() > 0;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"更新剪贴板历史失败：{ex.Message}");
            return false;
        }
    }

    private long ReadMaxTimestamp()
    {
        try
        {
            return Scalar("SELECT COALESCE(MAX(created_at), 0) FROM entries");
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>系统时钟毫秒级，同毫秒内多次复制会拿到相同时间；强制严格递增保证排序稳定。</summary>
    private long NextTimestamp()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (now <= _lastTimestampMs)
            now = _lastTimestampMs + 1;

        _lastTimestampMs = now;
        return now;
    }

    private void DeleteImageFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                File.Delete(Path.Combine(_imagesDirectory, file));
            }
            catch (Exception ex)
            {
                _log.Warn($"删除剪贴板图片失败：{ex.Message}");
            }
        }
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

    private static IReadOnlyList<string> ComputeAutoTags(
        ClipboardEntryKind kind,
        string? text,
        string preview,
        IReadOnlyList<string> paths) => kind switch
    {
        ClipboardEntryKind.Text => ClipboardClassifier.ClassifyText(text ?? preview),
        ClipboardEntryKind.Image => ClipboardClassifier.ClassifyImage(),
        ClipboardEntryKind.Files => ClipboardClassifier.ClassifyFiles(paths),
        _ => []
    };

    private static string ComputeHash(LegacyEntry entry) => entry.Kind switch
    {
        ClipboardEntryKind.Text when entry.Text is { Length: > 0 } => HashText(entry.Text),
        ClipboardEntryKind.Files when entry.Paths.Length > 0 => HashText(string.Join('\n', entry.Paths)),
        _ => HashText(entry.Preview)
    };

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

    private static string Serialize(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static string[] Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string QuoteFts(string value) =>
        "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed record EntryRow(
        string Id,
        ClipboardEntryKind Kind,
        string Preview,
        string? Text,
        string? ImageFile,
        string[] Paths,
        string ContentHash,
        long ByteSize,
        long CreatedAtMs,
        bool Favorite,
        string[] Tags,
        string[] AutoTags,
        string? SourceApp);

    private sealed record PreviousEntry(bool Favorite, string[] Tags);

    /// <summary>旧 JSONL 的行结构；字段名与旧版 camelCase 兼容。</summary>
    private sealed record LegacyEntry
    {
        public string Id { get; init; } = string.Empty;
        public ClipboardEntryKind Kind { get; init; }
        public string Preview { get; init; } = string.Empty;
        public string? Text { get; init; }
        public string? ImageFile { get; init; }
        public string[] Paths { get; init; } = [];
        public string ContentHash { get; init; } = string.Empty;
        public long ByteSize { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public bool Favorite { get; init; }
        public string[] Tags { get; init; } = [];
        public string[]? AutoTags { get; init; }
        public string? SourceApp { get; init; }
    }
}
