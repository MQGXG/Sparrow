// ClipboardHistoryStore 分部类：写入（文本/图片/文件）、清空、插入与裁剪。
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
    /// <summary>记录一段文本。与历史中的重复内容合并并刷新时间戳；收藏与标签跟着保留。</summary>
    public void AddText(
        string text,
        string? sourceApp = null,
        byte[]? html = null,
        byte[]? rtf = null)
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
                    sourceApp,
                    html,
                    rtf));

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

    private void InsertRow(SqliteConnection connection, SqliteTransaction transaction, EntryRow row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entries (
                id, kind, preview, text, image_file, paths,
                content_hash, byte_size, created_at, favorite, tags, auto_tags, source_app,
                html, rtf)
            VALUES (
                $id, $kind, $preview, $text, $image, $paths,
                $hash, $size, $created, $favorite, $tags, $auto, $source,
                $html, $rtf)
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
        command.Parameters.AddWithValue("$html", (object?)row.Html ?? DBNull.Value);
        command.Parameters.AddWithValue("$rtf", (object?)row.Rtf ?? DBNull.Value);

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
}
