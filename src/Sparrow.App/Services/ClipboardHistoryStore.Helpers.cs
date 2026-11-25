// ClipboardHistoryStore 分部类：标签规范化、哈希、序列化与嵌套行类型。
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
        string? SourceApp,
        byte[]? Html = null,
        byte[]? Rtf = null);

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
