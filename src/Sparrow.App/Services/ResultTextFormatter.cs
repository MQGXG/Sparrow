using System.Text;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.Services;

/// <summary>
/// 把「表格 / 文件列表」结果整理成可读文本。
///
/// 宿主的结果区是纯文本（可选中、可复制），所以表格用等宽对齐的方式落地：
/// 列按最大显示宽度补空格，表头下加一条分隔线。
/// 中文字符按两列宽计算，避免「文件」和「a.png」混排时整列错位。
/// </summary>
public static class ResultTextFormatter
{
    private const string ColumnSeparator = "  ";

    public static string Format(TablePayload table)
    {
        if (table.Columns.Count == 0)
            return string.Empty;

        var widths = table.Columns
            .Select(column => DisplayWidth(column))
            .ToArray();

        foreach (var row in table.Rows)
        {
            for (var index = 0; index < widths.Length; index++)
            {
                var cell = Cell(row, index);
                widths[index] = Math.Max(widths[index], DisplayWidth(cell));
            }
        }

        var builder = new StringBuilder();

        AppendRow(builder, table.Columns, widths);
        builder.AppendLine(string.Join(
            ColumnSeparator,
            widths.Select(width => new string('─', width))));

        foreach (var row in table.Rows)
            AppendRow(builder, Enumerable.Range(0, widths.Length).Select(index => Cell(row, index)), widths);

        return builder.ToString().TrimEnd();
    }

    /// <summary>文件列表结果：数量 + 每行一条路径。</summary>
    public static string Format(FileSetPayload files)
    {
        if (files.Paths.Count == 0)
            return "没有文件";

        var builder = new StringBuilder();
        builder.Append(files.Paths.Count).AppendLine(" 个文件");

        foreach (var path in files.Paths)
            builder.Append("- ").AppendLine(path);

        return builder.ToString().TrimEnd();
    }

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index < row.Count ? row[index] ?? string.Empty : string.Empty;

    private static void AppendRow(StringBuilder builder, IEnumerable<string> cells, int[] widths)
    {
        var values = cells.ToArray();

        for (var index = 0; index < widths.Length; index++)
        {
            var cell = index < values.Length ? values[index] ?? string.Empty : string.Empty;

            if (index > 0)
                builder.Append(ColumnSeparator);

            builder.Append(cell);

            // 最后一列不补空格，避免行尾空白
            if (index < widths.Length - 1)
                builder.Append(' ', widths[index] - DisplayWidth(cell));
        }

        builder.AppendLine();
    }

    /// <summary>显示宽度：CJK / 全角字符占两列，其余占一列。</summary>
    private static int DisplayWidth(string text)
    {
        var width = 0;

        foreach (var character in text)
            width += IsWide(character) ? 2 : 1;

        return width;
    }

    private static bool IsWide(char character) =>
        character >= 0x1100 &&
        (character <= 0x115F            // 谚文字母
         || character is >= '\u2E80' and <= '\uA4CF'   // CJK 部首 / 假名 / 汉字
         || character is >= '\uAC00' and <= '\uD7A3'   // 谚文音节
         || character is >= '\uF900' and <= '\uFAFF'   // CJK 兼容汉字
         || character is >= '\uFE30' and <= '\uFE4F'   // CJK 兼容形式
         || character is >= '\uFF00' and <= '\uFF60'   // 全角 ASCII / 标点
         || character is >= '\uFFE0' and <= '\uFFE6'); // 全角符号
}
