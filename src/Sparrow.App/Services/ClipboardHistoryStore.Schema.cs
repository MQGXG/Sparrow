// ClipboardHistoryStore 分部类：建库、FTS 索引与旧 JSONL 迁移。
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

public sealed partial class ClipboardHistoryStore
{
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
                    source_app   TEXT,
                    html         BLOB,
                    rtf          BLOB
                );

                CREATE INDEX IF NOT EXISTS ix_entries_created ON entries(created_at DESC);
                CREATE INDEX IF NOT EXISTS ix_entries_hash ON entries(kind, content_hash);
                CREATE INDEX IF NOT EXISTS ix_entries_favorite ON entries(favorite);
                """;
            schema.ExecuteNonQuery();
        }

        // user_version 1 的库没有富文本列：补列后升到 2
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            var current = Convert.ToInt64(version.ExecuteScalar() ?? 0L);

            if (current < 2)
            {
                if (current > 0)
                {
                    using var alter = connection.CreateCommand();
                    alter.CommandText = """
                        ALTER TABLE entries ADD COLUMN html BLOB;
                        ALTER TABLE entries ADD COLUMN rtf BLOB;
                        """;
                    alter.ExecuteNonQuery();
                }

                using var bump = connection.CreateCommand();
                bump.CommandText = "PRAGMA user_version = 2;";
                bump.ExecuteNonQuery();
            }
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
}
