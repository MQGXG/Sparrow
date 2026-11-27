namespace Sparrow.App.Services;

/// <summary>
/// 滚动截图的位移估计。
///
/// 纵向按「行哈希」、横向按「列哈希」把一帧压成一维序列，
/// 之后的模板匹配、多段投票、固定区域识别和交叉验证完全一样。
/// 因此这里只处理一维哈希数组，不关心帧的宽高和轴向。
/// </summary>
internal static class ScrollOffsetMatcher
{
    private const int Tolerance = 2;

    /// <summary>
    /// 从后往前扫，优先选「滚动量最小」的匹配：
    /// 页面里出现重复内容时，避免一次跳很远拼进重复片段。
    ///
    /// templateStart 由调用方给出：校准固定底栏（或右栏）后要从内容区末尾取模板，
    /// 否则模板永远取自固定区域，每次都只能依赖多段投票。
    /// </summary>
    public static (int Position, int Score) FindBestMatch(
        ulong[] previous, ulong[] current, int templateStart, int templateRows)
    {
        var bestPosition = -1;
        var bestScore = 0;

        for (var position = templateStart; position >= 0; position--)
        {
            var score = 0;
            for (var i = 0; i < templateRows; i++)
            {
                if (current[position + i] == previous[templateStart + i])
                    score++;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPosition = position;

                if (score == templateRows)
                    break;
            }
        }

        return (bestPosition, bestScore);
    }

    /// <summary>
    /// 多段投票估算滚动量。
    ///
    /// 把上一帧切成若干段，分别在新帧里找位置，再按段投票：
    /// 内容区各段会给出同一个滚动量，固定页眉 / 底栏给出 0。
    /// 固定区域直接排除在投票外：小选区里固定区域占的段数可能比内容还多，
    /// 让它们投 0 会把内容票压掉，表现就是「滚动一点就停」。
    /// </summary>
    public static int? EstimateOffset(ulong[] previous, ulong[] current, int extent)
    {
        var bandSize = Math.Clamp(extent / 12, 8, 40);
        if (bandSize >= extent)
            bandSize = Math.Max(1, extent / 2);

        var bandCount = Math.Clamp(extent / bandSize, 3, 8);
        if (bandCount <= 0 || bandSize <= 0)
            return null;

        // 两帧在同一位置连续相同的开头 / 结尾，就是固定页眉 / 底栏
        var fixedLeading = DetectFixedLeading(previous, current, extent);
        var contentEnd = extent - DetectFixedTrailing(previous, current, extent);

        var votes = new List<(int Offset, double Quality)>(bandCount);

        for (var index = 0; index < bandCount; index++)
        {
            var start = bandCount == 1
                ? 0
                : (int)((long)index * (extent - bandSize) / (bandCount - 1));

            // 完全落在固定区域里的段没有位移信息，不投票
            if (start < fixedLeading || start + bandSize > contentEnd)
                continue;

            // 纯色段没有位置信息，参与投票只会制造噪声
            if (DistinctCount(previous, start, bandSize) < Math.Max(3, bandSize / 4))
                continue;

            var (matchPosition, score, second) =
                FindBandMatch(previous, current, start, bandSize);

            if (matchPosition < 0)
                continue;

            var quality = (double)score / bandSize;
            if (quality < 0.6)
                continue;

            // 同一段在多个位置都同样匹配：重复内容，位置不可信
            if (second >= score - 1)
                continue;

            var offset = start - matchPosition;
            if (offset < 0 || offset > extent - bandSize)
                continue;

            votes.Add((offset, quality));
        }

        if (votes.Count == 0)
            return null;

        // 按容差聚合成桶，避免亚像素/重绘造成的 ±1 抖动被拆散
        var buckets = new List<(int Offset, double Support, int Count)>(votes.Count);

        foreach (var (voteOffset, quality) in votes.OrderByDescending(vote => vote.Quality))
        {
            var bucketIndex = buckets.FindIndex(
                bucket => Math.Abs(bucket.Offset - voteOffset) <= Tolerance);

            if (bucketIndex < 0)
            {
                buckets.Add((voteOffset, quality, 1));
                continue;
            }

            var bucket = buckets[bucketIndex];
            var count = bucket.Count + 1;

            buckets[bucketIndex] = (
                (int)Math.Round((bucket.Offset * (double)bucket.Count + voteOffset) / count),
                bucket.Support + quality,
                count);
        }

        var movement = default((int Offset, double Support, int Count));
        var still = default((int Offset, double Support, int Count));

        foreach (var bucket in buckets)
        {
            if (bucket.Offset >= 2)
            {
                if (bucket.Support > movement.Support)
                    movement = bucket;
            }
            else if (bucket.Support > still.Support)
            {
                still = bucket;
            }
        }

        // 内容区至少两段一致才认；只有一段时必须没有任何「没动」的证据
        if (movement.Count > 0
            && (movement.Count >= 2 || still.Count == 0)
            && (still.Count == 0 || movement.Support >= still.Support * 0.5))
        {
            return movement.Offset;
        }

        return still.Count > 0 ? 0 : null;
    }

    /// <summary>在一帧里找某一段的最佳位置，并返回次佳分数用于判断是否重复内容。</summary>
    public static (int Position, int Score, int Second) FindBandMatch(
        ulong[] previous, ulong[] current, int start, int size)
    {
        const int minimumSeparation = 4;

        var lastPosition = previous.Length - size;
        var bestPosition = -1;
        var bestScore = 0;
        var secondScore = 0;

        for (var position = 0; position <= lastPosition; position++)
        {
            var score = 0;
            for (var i = 0; i < size; i++)
            {
                if (current[position + i] == previous[start + i])
                    score++;
            }

            if (score > bestScore)
            {
                if (bestPosition >= 0 && Math.Abs(bestPosition - position) > minimumSeparation)
                    secondScore = Math.Max(secondScore, bestScore);

                bestScore = score;
                bestPosition = position;
            }
            else if (score > secondScore && Math.Abs(bestPosition - position) > minimumSeparation)
            {
                secondScore = score;
            }
        }

        return (bestPosition, bestScore, secondScore);
    }

    /// <summary>统计一段里有多少个不同的哈希，用来排除纯色区域。</summary>
    public static int DistinctCount(ulong[] hashes, int start, int count)
    {
        var seen = new HashSet<ulong>();

        for (var i = 0; i < count; i++)
            seen.Add(hashes[start + i]);

        return seen.Count;
    }

    /// <summary>从帧底（横向为右缘）往上（左）数，两帧在相同位置完全相同的单位数。</summary>
    public static int DetectFixedTrailing(ulong[] previous, ulong[] current, int extent)
    {
        var limit = Math.Min(extent - 1, extent / 2);
        var count = 0;

        while (count < limit
               && previous[extent - 1 - count] == current[extent - 1 - count])
        {
            count++;
        }

        return count;
    }

    /// <summary>从帧顶（横向为左缘）往下（右）数，两帧在相同位置完全相同的单位数。</summary>
    public static int DetectFixedLeading(ulong[] previous, ulong[] current, int extent)
    {
        var limit = Math.Min(extent - 1, extent / 3);
        var count = 0;

        while (count < limit && previous[count] == current[count])
            count++;

        return count;
    }

    /// <summary>用重叠区交叉验证多段投票的滚动量。</summary>
    public static bool Verify(ulong[] previous, ulong[] current, int offset, int extent)
    {
        var fixedLeading = DetectFixedLeading(previous, current, extent);
        var fixedTrailing = DetectFixedTrailing(previous, current, extent);
        var contentEnd = extent - fixedTrailing;

        if (offset >= contentEnd)
            return false;

        var overlap = contentEnd - offset;
        var start = Math.Min(fixedLeading, overlap);
        var total = overlap - start;

        // 重叠区去掉固定页眉后可能很短（小选区、滚动步长大）。
        // 硬性要求 12 行会把「页眉 + 底栏」的正常页面误判成失配。
        var minimum = Math.Clamp(extent / 16, 4, 12);
        if (total < minimum)
            return false;

        var matched = 0;
        for (var position = start; position < overlap; position++)
        {
            if (current[position] == previous[position + offset])
                matched++;
        }

        return matched >= total * 0.6;
    }
}
