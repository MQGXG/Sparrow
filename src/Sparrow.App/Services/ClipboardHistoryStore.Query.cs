// ClipboardHistoryStore 分部类：查询（最近/搜索/富文本/图片读取/标签与来源列表）。
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
    public IReadOnlyList<ClipboardHistoryEntry> Recent(int count) =>
        RecentCore(count, SelectColumns);

    /// <summary>
    /// 列表专用：文本只取前 2048 字符，完整文本用 <see cref="LoadText"/> 按需读取。
    /// 插件仍走 <see cref="Recent"/>，拿到的行为不变。
    /// </summary>
    public IReadOnlyList<ClipboardHistoryEntry> RecentForList(int count) =>
        RecentCore(count, ListColumns);

    private IReadOnlyList<ClipboardHistoryEntry> RecentCore(int count, string columns)
    {
        if (count <= 0)
            return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columns} FROM entries ORDER BY created_at DESC LIMIT $count";
        command.Parameters.AddWithValue("$count", count);

        return ReadEntries(command);
    }

    public IReadOnlyList<ClipboardHistoryEntry> Search(string keyword, int count) =>
        SearchCore(keyword, count, SelectColumns, SelectColumnsJoined);

    /// <summary>列表专用的搜索：同 <see cref="Search"/>，但文本只取前 2048 字符。</summary>
    public IReadOnlyList<ClipboardHistoryEntry> SearchForList(string keyword, int count) =>
        SearchCore(keyword, count, ListColumns, ListColumnsJoined);

    private IReadOnlyList<ClipboardHistoryEntry> SearchCore(
        string keyword,
        int count,
        string columns,
        string joinedColumns)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return RecentCore(count, columns);

        if (count <= 0)
            return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        // trigram 分词要求查询至少 3 个字符；短词和中文两字词走 LIKE
        if (_ftsEnabled && keyword.Length >= 3)
        {
            command.CommandText = $"""
                SELECT {joinedColumns}
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
                SELECT {columns}
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

    /// <summary>按 id 取回文本全文；列表查询只带前 2048 字符，复制/粘贴/详情用它补齐。</summary>
    public string? LoadText(string id)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT text FROM entries WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);

            return command.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取剪贴板文本失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>取回文本条目的原始 HTML / RTF；没有则为 null。</summary>
    public (byte[]? Html, byte[]? Rtf) LoadRichText(string id)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT html, rtf FROM entries WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return (null, null);

            var html = reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0);
            var rtf = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
            return (html, rtf);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>图片条目的磁盘路径；只按 id 查 image_file，不把路径暴露给插件。</summary>
    public string? ImagePath(string id)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT image_file FROM entries WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);

            return command.ExecuteScalar() is string file && !string.IsNullOrWhiteSpace(file)
                ? Path.Combine(_imagesDirectory, file)
                : null;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取剪贴板图片路径失败：{ex.Message}");
            return null;
        }
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
}
