using System.Text;

namespace Sparrow.Plugin.Ocr;

/// <summary>一个单元格的包围盒（图片像素坐标）。</summary>
public readonly record struct TableGridCell(int X, int Y, int Width, int Height);

/// <summary>一段文字及其包围盒（图片像素坐标）。</summary>
public readonly record struct TableTextBlock(string Text, int X, int Y, int Width, int Height);

/// <summary>
/// 把表格结构（单元格序列）和文字识别结果合成为一张完整的 HTML 表格。
///
/// SLANet 只输出单元格结构（每个 &lt;td&gt; 一把框），格子里的文字它不管；
/// 文字必须由文字模型识别后按「中心落在哪个格子里」回填。
/// 两边的框都在原图坐标系，直接按几何包含关系匹配即可。
///
/// 单独放在插件工程里、不依赖 Paddle 类型，是为了能在测试里覆盖；
/// worker 通过 Compile Include 链接这份源码，保证只有一份实现。
/// </summary>
public static class TableHtmlBuilder
{
    /// <summary>
    /// 合成 HTML。结构标签按原顺序输出，文字填进对应的单元格。
    ///
    /// Sdcb 输出的 <c>&lt;td&gt;</c> 有三种形态：
    ///   - <c>&lt;td&gt;&lt;/td&gt;</c>：整格一个 token；
    ///   - <c>&lt;td&gt;</c>：开始标签，后面跟 <c>&lt;/td&gt;</c>；
    ///   - <c>&lt;td</c> + 属性 token + <c>&gt;</c>：带 colspan/rowspan 的格子。
    /// 三种都要把文字放到单元格内容位置。
    /// </summary>
    public static string Compose(
        IReadOnlyList<string> htmlTags,
        IReadOnlyList<TableGridCell> cells,
        IReadOnlyList<TableTextBlock> blocks)
    {
        var cellTexts = AssignTexts(cells, blocks);

        // SLANet 输出的是 <tr>/<td> 片段，不带外层 <table>，这里补齐
        var builder = new StringBuilder("<table>");
        var cellIndex = 0;
        var pendingCell = false;

        foreach (var tag in htmlTags)
        {
            if (tag == "<td></td>")
            {
                builder.Append("<td>").Append(CellText(cellTexts, cellIndex++)).Append("</td>");
                continue;
            }

            if (tag is "<td>" or "<td")
            {
                pendingCell = true;
                builder.Append(tag);
                continue;
            }

            if (tag == "</td>")
            {
                if (pendingCell)
                {
                    builder.Append(CellText(cellTexts, cellIndex++));
                    pendingCell = false;
                }

                builder.Append(tag);
                continue;
            }

            builder.Append(tag);
        }

        builder.Append("</table>");
        return builder.ToString();
    }

    /// <summary>把每段文字分配到所属单元格；同一格内按阅读顺序（先上后下、先左后右）排序。</summary>
    private static List<string>[] AssignTexts(
        IReadOnlyList<TableGridCell> cells,
        IReadOnlyList<TableTextBlock> blocks)
    {
        var buckets = new List<(int Y, int X, string Text)>[cells.Count];
        for (var i = 0; i < buckets.Length; i++)
            buckets[i] = [];

        foreach (var block in blocks)
        {
            var text = (block.Text ?? string.Empty).Trim();
            if (text.Length == 0 || block.Width <= 0 || block.Height <= 0)
                continue;

            var index = FindCell(cells, block);
            if (index >= 0)
                buckets[index].Add((block.Y, block.X, text));
        }

        var result = new List<string>[cells.Count];

        for (var i = 0; i < buckets.Length; i++)
        {
            // 单元格内多行文字用空格连接，避免撑破 Markdown 表格
            result[i] = buckets[i]
                .OrderBy(item => item.Y)
                .ThenBy(item => item.X)
                .Select(item => item.Text)
                .ToList();
        }

        return result;
    }

    /// <summary>单元格文字：转义后连接，空单元格返回空串。</summary>
    private static string CellText(List<string>[] cellTexts, int index)
    {
        if (index < 0 || index >= cellTexts.Length || cellTexts[index].Count == 0)
            return string.Empty;

        return Escape(string.Join(' ', cellTexts[index]));
    }

    /// <summary>找文字块所属的单元格：先看中心点，中心落在框外时退回重叠面积。</summary>
    private static int FindCell(IReadOnlyList<TableGridCell> cells, TableTextBlock text)
    {
        var centerX = text.X + text.Width / 2;
        var centerY = text.Y + text.Height / 2;

        var best = -1;
        long bestArea = long.MaxValue;

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.Width <= 0 || cell.Height <= 0)
                continue;

            if (centerX < cell.X || centerX > cell.X + cell.Width ||
                centerY < cell.Y || centerY > cell.Y + cell.Height)
            {
                continue;
            }

            // 框可能互相包含（合并单元格），取最紧的那个
            long area = (long)cell.Width * cell.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = i;
            }
        }

        if (best >= 0)
            return best;

        // 文字框有倾斜、贴边时中心可能刚好出界，用重叠面积兜底
        long overlap = 0;

        for (var i = 0; i < cells.Count; i++)
        {
            long area = Intersect(text, cells[i]);
            if (area > overlap)
            {
                overlap = area;
                best = i;
            }
        }

        return best;
    }

    private static long Intersect(TableTextBlock a, TableGridCell b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (right <= left || bottom <= top)
            return 0;

        return (long)(right - left) * (bottom - top);
    }

    /// <summary>文字进 HTML 前必须转义，否则一个 "&lt;" 就能让表格结构失效。</summary>
    private static string Escape(string text)
    {
        if (text.AsSpan().IndexOfAny('&', '<', '>') < 0)
            return text;

        var builder = new StringBuilder(text.Length + 8);

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }
}
