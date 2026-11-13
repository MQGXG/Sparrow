using System.Text;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Ocr;

/// <summary>
/// 把 OCR 的文本块按坐标还原成可读文本。
///
/// 为什么需要它：PP-OCR 的 <c>result.Text</c> 是所有文本块的直接拼接，
/// 空格和换行全丢（实测输出 "HELLOOCR麻雀123"）。
/// 按行聚类 + 行内按 X 排序，才能得到 "HELLO OCR 麻雀 123"。
///
/// 算法与 OmniSeeker 的 <c>OcrTextSorter.ByLine</c> 一致：
/// 用文本块中心点的 Y 距离判断是否同一行，阈值取平均行高的一半。
/// </summary>
public static class OcrTextComposer
{
    public static string Compose(IReadOnlyList<OcrRegion> regions, float minimumScore = 0.5f)
    {
        var lines = ComposeLines(regions, minimumScore);
        return string.Join(Environment.NewLine, lines.Select(line => line.Text));
    }

    /// <summary>
    /// 按整理模式合成全文。行框始终用默认聚类（<see cref="ComposeLines"/>），
    /// 保证截图叠加层的「点行复制」在所有模式下都指向同一套行。
    /// </summary>
    public static string ComposeText(
        IReadOnlyList<OcrRegion> regions,
        OcrLayoutMode mode,
        string fallback,
        float minimumScore = 0.5f)
    {
        return mode switch
        {
            OcrLayoutMode.Raw => fallback,
            OcrLayoutMode.Position => ComposePosition(regions, minimumScore),
            OcrLayoutMode.Table => ComposeTable(regions, minimumScore),
            _ => Compose(regions, minimumScore)
        };
    }

    /// <summary>
    /// 按位置排布：空格近似还原每个块的水平位置，保留原始排版观感。
    /// 块的像素 Left 映射到字符列，行宽按内容总量自适应（上限 200 列）。
    /// </summary>
    public static string ComposePosition(IReadOnlyList<OcrRegion> regions, float minimumScore = 0.5f)
    {
        var valid = Filter(regions, minimumScore);
        if (valid.Count == 0)
            return string.Empty;

        var lines = GroupByLine(valid, LineThresholdFactor);
        if (lines.Count == 0)
            return string.Empty;

        var minLeft = valid.Min(r => r.X);
        var totalWidth = Math.Max(1.0, valid.Max(r => r.X + r.Width) - minLeft);
        var totalTextLength = valid.Sum(r => r.Text.Length);
        var maxCharsPerLine = Math.Clamp(totalTextLength / lines.Count, 60, 200);

        var result = new StringBuilder();

        foreach (var line in lines)
        {
            var ordered = line.OrderBy(r => r.X).ToList();
            var lineText = new StringBuilder();
            var currentPos = 0;

            for (var i = 0; i < ordered.Count; i++)
            {
                var region = ordered[i];
                var targetPos = (int)((region.X - minLeft) / totalWidth * maxCharsPerLine);
                targetPos = Math.Max(currentPos, targetPos);

                var spacesToAdd = targetPos - currentPos;
                if (i > 0 && spacesToAdd < 2)
                    spacesToAdd = 2;

                lineText.Append(' ', spacesToAdd);
                lineText.Append(region.Text.Trim());
                currentPos = targetPos + region.Text.Length;
            }

            result.AppendLine(lineText.ToString().TrimEnd());
        }

        return result.ToString().TrimEnd();
    }

    /// <summary>
    /// 表格模式：行判定阈值收紧一半，防止相邻表格行被并成一行；
    /// 列间用 Tab 分隔，粘贴到 Excel 时自动分列。
    /// </summary>
    public static string ComposeTable(IReadOnlyList<OcrRegion> regions, float minimumScore = 0.5f)
    {
        var valid = Filter(regions, minimumScore);
        if (valid.Count == 0)
            return string.Empty;

        var lines = GroupByLine(valid, TableThresholdFactor);

        return string.Join(
            Environment.NewLine,
            lines.Select(line => string.Join(
                "\t",
                line.OrderBy(r => r.X).Select(r => r.Text.Trim()))));
    }

    /// <summary>
    /// 与 <see cref="Compose"/> 共用同一套聚类结果，额外返回每行的包围盒。
    /// 行框取该行所有文本块的并集，顺序与文本行一致。
    /// </summary>
    public static IReadOnlyList<OcrTextLine> ComposeLines(
        IReadOnlyList<OcrRegion> regions,
        float minimumScore = 0.5f)
    {
        var valid = Filter(regions, minimumScore);
        if (valid.Count == 0)
            return [];

        var lines = GroupByLine(valid, LineThresholdFactor);

        return lines.Select(line =>
        {
            var ordered = line.OrderBy(r => r.X).ToList();
            var text = string.Join(" ", ordered.Select(r => r.Text.Trim()));
            var left = ordered.Min(r => r.X);
            var top = ordered.Min(r => r.Y);
            var right = ordered.Max(r => r.X + r.Width);
            var bottom = ordered.Max(r => r.Y + r.Height);

            return new OcrTextLine(text, left, top, right - left, bottom - top);
        }).ToList();
    }

    /// <summary>普通行聚类的阈值系数：中心点距离 &lt; 平均行高 × 该系数视为同一行。</summary>
    private const double LineThresholdFactor = 0.5;

    /// <summary>表格模式的阈值：收紧一半，防止相邻表格行被误并。</summary>
    private const double TableThresholdFactor = 0.25;

    /// <summary>低分块通常是标点碎片或噪点，先过滤再聚类。</summary>
    private static List<OcrRegion> Filter(IReadOnlyList<OcrRegion> regions, float minimumScore) =>
        regions
            .Where(r => r.Score >= minimumScore && !string.IsNullOrWhiteSpace(r.Text))
            .ToList();

    /// <summary>按垂直中心点把文本块聚成行，行顺序按首次出现的位置。</summary>
    private static List<List<OcrRegion>> GroupByLine(
        IReadOnlyList<OcrRegion> regions,
        double thresholdFactor)
    {
        var ordered = regions
            .OrderBy(r => r.Y + r.Height / 2.0)
            .ThenBy(r => r.X)
            .ToList();

        var averageHeight = ordered.Average(r => r.Height);
        var threshold = Math.Max(1.0, averageHeight * thresholdFactor);

        var lines = new List<List<OcrRegion>>();

        foreach (var region in ordered)
        {
            var center = region.Y + region.Height / 2.0;
            List<OcrRegion>? target = null;

            foreach (var line in lines)
            {
                var lineCenter = line.Average(r => r.Y + r.Height / 2.0);
                if (Math.Abs(lineCenter - center) <= threshold)
                {
                    target = line;
                    break;
                }
            }

            if (target is null)
                lines.Add([region]);
            else
                target.Add(region);
        }

        return lines;
    }
}
