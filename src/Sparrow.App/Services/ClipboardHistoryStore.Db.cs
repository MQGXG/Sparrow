// ClipboardHistoryStore 分部类：连接、标量/执行、时间戳与图片文件清理。
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
}
