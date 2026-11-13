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
    /// 与 <see cref="Compose"/> 共用同一套聚类结果，额外返回每行的包围盒。
    /// 行框取该行所有文本块的并集，顺序与文本行一致。
    /// </summary>
    public static IReadOnlyList<OcrTextLine> ComposeLines(
        IReadOnlyList<OcrRegion> regions,
        float minimumScore = 0.5f)
    {
        // 低分块通常是标点碎片或噪点（实测俄语图会多出一个 "_"，score 0.36）
        var valid = regions
            .Where(r => r.Score >= minimumScore && !string.IsNullOrWhiteSpace(r.Text))
            .ToList();

        if (valid.Count == 0)
            return [];

        var lines = GroupByLine(valid);

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

    /// <summary>按垂直中心点把文本块聚成行，行顺序按首次出现的位置。</summary>
    private static List<List<OcrRegion>> GroupByLine(IReadOnlyList<OcrRegion> regions)
    {
        var ordered = regions
            .OrderBy(r => r.Y + r.Height / 2.0)
            .ThenBy(r => r.X)
            .ToList();

        var averageHeight = ordered.Average(r => r.Height);
        var threshold = Math.Max(1.0, averageHeight * 0.5);

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
