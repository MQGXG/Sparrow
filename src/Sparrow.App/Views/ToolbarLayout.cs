namespace Sparrow.App.Views;

/// <summary>
/// 工具条布局：Positions 是每个槽位的工具顺序，槽位最后一项是折叠组入口；
/// Hidden 是显式隐藏的工具。
///
/// 空配置回落默认，未知 id / 重复 id / 损坏数据都跳过，保证老配置和手改配置都能起来。
/// </summary>
internal sealed class ToolbarLayout
{
    /// <summary>四个折叠组的默认成员；最后一个就是入口。</summary>
    public static readonly AnnotationTool[][] Groups =
    [
        [AnnotationTool.Diamond, AnnotationTool.Ellipse, AnnotationTool.Bracket, AnnotationTool.Rectangle],
        [AnnotationTool.Line, AnnotationTool.Distance, AnnotationTool.Angle, AnnotationTool.Arrow],
        [AnnotationTool.Highlighter, AnnotationTool.Pen],
        [AnnotationTool.Number, AnnotationTool.Watermark, AnnotationTool.Bubble, AnnotationTool.Text]
    ];

    public static readonly AnnotationTool[][] DefaultPositions =
    [
        Groups[0],
        Groups[1],
        Groups[2],
        [AnnotationTool.Mosaic],
        [AnnotationTool.Eraser],
        [AnnotationTool.Spotlight],
        [AnnotationTool.Magnifier],
        Groups[3]
    ];

    public List<List<AnnotationTool>> Positions { get; } = [];

    public HashSet<AnnotationTool> Hidden { get; } = [];

    public static ToolbarLayout Default() => Parse(null, null);

    public static ToolbarLayout Parse(IEnumerable<string>? positions, IEnumerable<string>? hidden)
    {
        var layout = new ToolbarLayout();

        if (positions is not null)
        {
            foreach (var entry in positions)
            {
                var tools = new List<AnnotationTool>();

                foreach (var raw in (entry ?? string.Empty).Split(
                             '|',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Enum.TryParse<AnnotationTool>(raw, true, out var tool)
                        || tool == AnnotationTool.None
                        || layout.Contains(tool)
                        || tools.Contains(tool))
                        continue;

                    tools.Add(tool);
                }

                if (tools.Count > 0)
                    layout.Positions.Add(tools);
            }
        }

        if (layout.Positions.Count == 0)
            layout.Positions.AddRange(DefaultPositions.Select(position => position.ToList()));

        var placed = layout.Positions.SelectMany(position => position).ToHashSet();

        if (hidden is not null)
        {
            foreach (var raw in hidden)
            {
                if (Enum.TryParse<AnnotationTool>(raw, true, out var tool)
                    && tool != AnnotationTool.None
                    && placed.Contains(tool))
                    layout.Hidden.Add(tool);
            }
        }

        // 位置里没出现过的工具同样算隐藏（兼容手改配置）
        foreach (var tool in Enum.GetValues<AnnotationTool>())
        {
            if (tool != AnnotationTool.None && !placed.Contains(tool))
                layout.Hidden.Add(tool);
        }

        // 菱形是后加的工具：老配置里没提到它时自动补回形状组，而不是被当成隐藏
        var configMentionsDiamond = positions?.Any(entry =>
            entry?.Contains(nameof(AnnotationTool.Diamond), StringComparison.OrdinalIgnoreCase) == true) ?? false;

        if (positions is not null && !configMentionsDiamond && !layout.Contains(AnnotationTool.Diamond))
        {
            layout.Hidden.Remove(AnnotationTool.Diamond);

            var shapes = layout.Positions.FirstOrDefault(position =>
                position.Any(tool => GroupIndexFor(tool) == 0));

            if (shapes is not null)
                shapes.Add(AnnotationTool.Diamond);
            else
                layout.Positions.Add([AnnotationTool.Diamond]);
        }

        return layout;
    }

    public List<string> SerializePositions() =>
        Positions.Select(position => string.Join('|', position)).ToList();

    public List<string> SerializeHidden() =>
        Hidden.Select(tool => tool.ToString()).OrderBy(name => name, StringComparer.Ordinal).ToList();

    public bool Contains(AnnotationTool tool) =>
        Positions.Any(position => position.Contains(tool));

    public bool IsVisible(AnnotationTool tool) => !Hidden.Contains(tool);

    public void Toggle(AnnotationTool tool)
    {
        if (!Hidden.Remove(tool))
            Hidden.Add(tool);
    }

    public void MovePosition(int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= Positions.Count || target < 0 || target >= Positions.Count)
            return;

        (Positions[index], Positions[target]) = (Positions[target], Positions[index]);
    }

    /// <summary>把工具移到所在槽位末尾，使它成为折叠组入口。</summary>
    public void SetEntry(AnnotationTool tool)
    {
        var position = Positions.FirstOrDefault(item => item.Contains(tool));
        if (position is null || position[^1] == tool)
            return;

        position.Remove(tool);
        position.Add(tool);
    }

    public static int GroupIndexFor(AnnotationTool tool)
    {
        for (var index = 0; index < Groups.Length; index++)
        {
            if (Groups[index].Contains(tool))
                return index;
        }

        return -1;
    }

    public static string GroupLabel(int index) => index switch
    {
        0 => "形状",
        1 => "箭头",
        2 => "画笔",
        3 => "文字",
        _ => string.Empty
    };

    public static IEnumerable<AnnotationTool> AllTools() =>
        DefaultPositions.SelectMany(position => position);
}
