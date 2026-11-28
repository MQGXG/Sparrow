using Avalonia.LogicalTree;
using Shouldly;
using Sparrow.Plugin.Ui;
using Sparrow.Plugin.Video.Trim;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// P4-B：视频裁剪插件的自定义表面。
/// 视图只组合宿主控件、读写参数；执行逻辑仍走 <c>ExecuteAsync</c>，这里不碰 ffmpeg。
/// </summary>
public sealed class VideoTrimViewTests
{
    /// <summary>记录写入的假上下文，验证视图确实把交互同步回参数。</summary>
    private sealed class RecordingViewContext : IToolViewContext
    {
        private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

        public List<(string Key, string? Value)> Writes { get; } = [];

        public string? GetParameter(string key) => _parameters.GetValueOrDefault(key);

        public void SetParameter(string key, string? value)
        {
            _parameters[key] = value ?? string.Empty;
            Writes.Add((key, value));
        }

        public IReadOnlyList<string> InputFiles => [];
    }

    [Fact]
    public void 视频裁剪工具_实现IToolViewFactory()
    {
        new VideoTrimTool().ShouldBeAssignableTo<IToolViewFactory>();
    }

    [Fact]
    public void 视频裁剪视图_读取参数并组合宿主控件()
    {
        var context = new RecordingViewContext();
        context.SetParameter("start", "2.5");
        context.SetParameter("end", "12");

        var view = new VideoTrimView(context);

        var section = view.GetLogicalDescendants()
            .OfType<PluginSection>()
            .ShouldHaveSingleItem();
        section.Title.ShouldBe("裁剪区间");

        var timeline = view.GetLogicalDescendants()
            .OfType<PluginTimeline>()
            .ShouldHaveSingleItem();
        timeline.Start.ShouldBe(2.5);
        timeline.End.ShouldBe(12);
        timeline.Duration.ShouldBe(60, "没有任何时长 API 时按 60 秒轨道兜底");

        view.GetLogicalDescendants().OfType<PluginHint>().ShouldNotBeEmpty();
        view.GetLogicalDescendants().OfType<PluginNumberField>().Count().ShouldBe(2);
    }

    [Fact]
    public void 视频裁剪视图_参数缺失时用默认区间()
    {
        var view = new VideoTrimView(new RecordingViewContext());

        var timeline = view.GetLogicalDescendants()
            .OfType<PluginTimeline>()
            .ShouldHaveSingleItem();

        timeline.Start.ShouldBe(0);
        timeline.End.ShouldBe(10);
        timeline.Duration.ShouldBe(60);
    }

    [Fact]
    public void 拖动时间轴_用不变文化写回参数()
    {
        var context = new RecordingViewContext();
        context.SetParameter("start", "0");
        context.SetParameter("end", "10");

        var view = new VideoTrimView(context);
        var timeline = view.GetLogicalDescendants()
            .OfType<PluginTimeline>()
            .ShouldHaveSingleItem();

        timeline.Start = 3.5;

        context.GetParameter("start").ShouldBe("3.5");
        context.GetParameter("end").ShouldBe("10");

        timeline.End = 4;

        context.GetParameter("end").ShouldBe("4");

        // 起点越过终点时时间轴会推着终点走，参数必须反映最终区间。
        timeline.Start = 9;

        context.GetParameter("start").ShouldBe("9");
        context.GetParameter("end").ShouldBe("9");
    }

    [Fact]
    public void 数字输入_与时间轴双向同步()
    {
        var context = new RecordingViewContext();
        context.SetParameter("start", "0");
        context.SetParameter("end", "10");

        var view = new VideoTrimView(context);
        var timeline = view.GetLogicalDescendants()
            .OfType<PluginTimeline>()
            .ShouldHaveSingleItem();
        var fields = view.GetLogicalDescendants()
            .OfType<PluginNumberField>()
            .ToList();

        // 输入终点：时间轴跟着走，参数同步写回。
        fields[1].Value = 25m;

        timeline.End.ShouldBe(25);
        context.GetParameter("end").ShouldBe("25");

        // 输入超过初始轨道长度：轨道变长，不把用户输入悄悄夹回 60。
        fields[1].Value = 90m;

        timeline.Duration.ShouldBe(90);
        timeline.End.ShouldBe(90);
        context.GetParameter("end").ShouldBe("90");

        // 反向：外部改时间轴，输入框跟着变。
        timeline.Start = 5;

        fields[0].Value.ShouldBe(5m);
    }

    [Fact]
    public void 数字输入_非法起点被夹回零()
    {
        var context = new RecordingViewContext();
        context.SetParameter("start", "0");
        context.SetParameter("end", "10");

        var view = new VideoTrimView(context);
        var timeline = view.GetLogicalDescendants()
            .OfType<PluginTimeline>()
            .ShouldHaveSingleItem();
        var fields = view.GetLogicalDescendants()
            .OfType<PluginNumberField>()
            .ToList();

        // NumericUpDown 自己会按 Minimum 夹；这里直接给属性非法值，验证视图侧兜底。
        fields[0].Value = -3m;

        timeline.Start.ShouldBe(0);
        fields[0].Value.ShouldBe(0m);
        context.GetParameter("start").ShouldBe("0");
    }
}
