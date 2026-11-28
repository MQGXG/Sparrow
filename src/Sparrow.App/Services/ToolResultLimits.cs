using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.Services;

/// <summary>
/// 结果与历史的硬上限。
///
/// 插件可以返回任意大的结果，宿主必须在进入界面 / 落盘前收口：
/// 一个 200 MB 的文本结果足以把进程内存和历史文件同时撑爆，
/// 而这不是某个插件的局部问题——同一个进程里所有工具都会受 GC 影响。
///
/// 超限时保留可读前缀并显式标记，不静默丢弃。
/// </summary>
public static class ToolResultLimits
{
    /// <summary>单条文本 / SVG / OCR 文本的字符上限。</summary>
    public const int MaxTextChars = 1_000_000;

    /// <summary>表格最多保留多少行数据（不含表头）。</summary>
    public const int MaxTableRows = 10_000;

    /// <summary>表格最多保留多少列。</summary>
    public const int MaxTableColumns = 64;

    /// <summary>文件集最多保留多少条路径。</summary>
    public const int MaxFilePaths = 2_000;

    /// <summary>图片结果字节上限；超过直接拒绝，避免宿主再复制一份。</summary>
    public const int MaxImageBytes = 32 * 1024 * 1024;

    public const string TruncatedSuffix = "\n\n…（结果超过大小上限，已截断）";

    public const string TruncatedNotice = "结果超过大小上限，已截断";

    /// <summary>统一收口：成功结果按载荷类型套上限；失败结果原样返回。</summary>
    public static ToolResult Apply(ToolResult result)
    {
        if (!result.Success || result.Payload is null)
            return result;

        return result.Payload switch
        {
            TextPayload text => ApplyText(result, text),
            OcrResultPayload ocr => ApplyOcr(result, ocr),
            SvgPayload svg => ApplySvg(result, svg),
            TablePayload table => ApplyTable(result, table),
            FileSetPayload files => ApplyFiles(result, files),
            ImagePayload image => ApplyImage(result, image),
            _ => result
        };
    }

    /// <summary>
    /// 按字符截断并追加可见后缀。不会把代理对切成半个字符。
    /// </summary>
    public static string TruncateText(string text, int maxChars, out bool truncated)
    {
        if (text.Length <= maxChars)
        {
            truncated = false;
            return text;
        }

        // 后缀也算进上限：否则只超一点点时，截断后的文本反而更长
        var keep = Math.Max(0, maxChars - TruncatedSuffix.Length);
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
            keep--;

        truncated = true;
        return text[..keep] + TruncatedSuffix;
    }

    private static ToolResult ApplyText(ToolResult result, TextPayload payload)
    {
        var text = TruncateText(payload.Text, MaxTextChars, out var truncated);

        return truncated
            ? result with
            {
                Payload = payload with { Text = text, IsTruncated = true },
                Notice = TruncatedNotice
            }
            : result;
    }

    private static ToolResult ApplyOcr(ToolResult result, OcrResultPayload payload)
    {
        var text = TruncateText(payload.Text, MaxTextChars, out var truncated);

        return truncated
            ? result with
            {
                Payload = payload with { Text = text },
                Notice = TruncatedNotice
            }
            : result;
    }

    private static ToolResult ApplySvg(ToolResult result, SvgPayload payload)
    {
        var svg = TruncateText(payload.Svg, MaxTextChars, out var truncated);

        return truncated
            ? result with
            {
                Payload = payload with { Svg = svg },
                Notice = TruncatedNotice
            }
            : result;
    }

    private static ToolResult ApplyTable(ToolResult result, TablePayload payload)
    {
        var columnCount = Math.Min(payload.Columns.Count, MaxTableColumns);
        var columns = columnCount == payload.Columns.Count
            ? payload.Columns
            : payload.Columns.Take(columnCount).ToList();

        var rowCount = Math.Min(payload.Rows.Count, MaxTableRows);
        var rows = new List<IReadOnlyList<string>>(rowCount);

        var cellTruncated = false;
        for (var index = 0; index < rowCount; index++)
        {
            var row = payload.Rows[index];
            if (row.Count > MaxTableColumns)
            {
                cellTruncated = true;
                rows.Add(row.Take(MaxTableColumns).ToList());
            }
            else
            {
                rows.Add(row);
            }
        }

        var truncated = columnCount < payload.Columns.Count
                        || rowCount < payload.Rows.Count
                        || cellTruncated;

        return truncated
            ? result with
            {
                Payload = payload with { Columns = columns, Rows = rows },
                Notice = TruncatedNotice
            }
            : result;
    }

    private static ToolResult ApplyFiles(ToolResult result, FileSetPayload payload)
    {
        if (payload.Paths.Count <= MaxFilePaths)
            return result;

        return result with
        {
            Payload = payload with { Paths = payload.Paths.Take(MaxFilePaths).ToList() },
            Notice = TruncatedNotice
        };
    }

    private static ToolResult ApplyImage(ToolResult result, ImagePayload payload)
    {
        if (payload.Data.Length <= MaxImageBytes)
            return result;

        var megabytes = payload.Data.Length / 1024.0 / 1024.0;
        return ToolResult.Failed(
            $"图片结果过大（{megabytes:F1} MB，上限 {MaxImageBytes / 1024 / 1024} MB），已拒绝以保护内存");
    }
}
