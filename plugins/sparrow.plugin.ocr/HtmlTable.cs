using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Sparrow.Plugin.Ocr;

/// <summary>
/// HTML 表格 → Markdown 管道表格。
///
/// 为什么不让模型直接出 Markdown：实测 MiMo 不管提示词怎么写都会回 HTML，
/// 与其反复调提示词，不如统一要 HTML，在本地做一次确定性转换。
///
/// 只处理模型实际会产出的形态：&lt;table&gt;/&lt;tr&gt;/&lt;th&gt;/&lt;td&gt;、rowspan/colspan、
/// &lt;br&gt; 和常见实体；不追求完整 HTML 解析（那该用专门的解析器）。
/// </summary>
internal static partial class HtmlTable
{
    [GeneratedRegex(@"<table[^>]*>(?<body>.*?)</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"<tr[^>]*>(?<cells>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<(?<tag>th|td)(?<attrs>[^>]*)>(?<content>.*?)</\k<tag>>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"rowspan\s*=\s*[""']?(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RowSpanRegex();

    [GeneratedRegex(@"colspan\s*=\s*[""']?(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ColSpanRegex();

    /// <summary>
    /// 转换；输入里没有表格时返回 null（调用方可以原样返回或报错）。
    /// </summary>
    public static string? ToMarkdown(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var table = TableRegex().Match(html);
        if (!table.Success)
            return null;

        var rows = new List<List<string>>();
        var spans = new List<List<(int RowSpan, int ColSpan)>>();

        foreach (Match row in RowRegex().Matches(table.Groups["body"].Value))
        {
            var cells = new List<string>();
            var rowSpans = new List<(int, int)>();

            foreach (Match cell in CellRegex().Matches(row.Groups["cells"].Value))
            {
                cells.Add(Normalize(cell.Groups["content"].Value));
                rowSpans.Add((Span(RowSpanRegex(), cell.Groups["attrs"].Value),
                              Span(ColSpanRegex(), cell.Groups["attrs"].Value)));
            }

            if (cells.Count > 0)
            {
                rows.Add(cells);
                spans.Add(rowSpans);
            }
        }

        if (rows.Count == 0)
            return null;

        // 按 rowspan/colspan 展开成矩形：被占用的格子留空
        var width = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var column = 0;
            for (var c = 0; c < rows[r].Count; c++)
            {
                column += spans[r][c].ColSpan;
            }
            width = Math.Max(width, column);
        }

        if (width == 0)
            return null;

        var grid = new string[rows.Count, width];
        for (var r = 0; r < rows.Count; r++)
        for (var c = 0; c < width; c++)
            grid[r, c] = string.Empty;

        for (var r = 0; r < rows.Count; r++)
        {
            var column = 0;
            for (var c = 0; c < rows[r].Count && column < width; c++)
            {
                while (column < width && grid[r, column].Length > 0)
                    column++;

                if (column >= width)
                    break;

                var (rowSpan, colSpan) = spans[r][c];
                grid[r, column] = rows[r][c];

                for (var dr = 0; dr < rowSpan && r + dr < rows.Count; dr++)
                for (var dc = 0; dc < colSpan && column + dc < width; dc++)
                {
                    if (dr == 0 && dc == 0)
                        continue;

                    // 合并区域内的其余格子留空，避免 Markdown 表达不了被误读
                    grid[r + dr, column + dc] = " ";
                }

                column += colSpan;
            }
        }

        var builder = new StringBuilder();

        for (var r = 0; r < rows.Count; r++)
        {
            builder.Append('|');
            for (var c = 0; c < width; c++)
                builder.Append(' ').Append(grid[r, c].Trim()).Append(" |");
            builder.Append('\n');

            if (r == 0)
            {
                builder.Append('|');
                for (var c = 0; c < width; c++)
                    builder.Append(" --- |");
                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static int Span(Regex regex, string attrs)
    {
        var match = regex.Match(attrs);
        return match.Success && int.TryParse(match.Groups["n"].Value, out var value) && value > 1
            ? Math.Min(value, 20)
            : 1;
    }

    /// <summary>去标签、把 &lt;br&gt; 换成空格、解实体、转义竖线。</summary>
    private static string Normalize(string html)
    {
        var text = BreakRegex().Replace(html, " ");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ");

        return text.Replace("|", "\\|");
    }
}
