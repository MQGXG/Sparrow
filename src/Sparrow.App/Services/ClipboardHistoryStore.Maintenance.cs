// ClipboardHistoryStore 分部类：收藏、标签、删除与恢复。
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
}
