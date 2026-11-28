using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Shouldly;
using Sparrow.Plugin.Ui;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// P4-B：宿主区间选择条（<see cref="PluginTimeline"/>）的夹取规则与拖动行为。
/// 只验证区间数学与命中，不涉及媒体 / 波形。
/// </summary>
public sealed class PluginTimelineTests
{
    [Fact]
    public void 时间轴_起止被夹在时长内()
    {
        var timeline = new PluginTimeline { Duration = 60 };

        timeline.Start = 70;

        timeline.Start.ShouldBe(60);
        timeline.End.ShouldBe(60);
    }

    [Fact]
    public void 时间轴_负值夹到零()
    {
        var timeline = new PluginTimeline { Duration = 60, Start = 10, End = 20 };

        timeline.Start = -5;
        timeline.Start.ShouldBe(0);

        timeline.End = -2;
        timeline.End.ShouldBe(0);
        timeline.Start.ShouldBe(0);
    }

    [Fact]
    public void 时间轴_结束早于开始时自动纠正()
    {
        var timeline = new PluginTimeline { Duration = 60, Start = 8, End = 20 };

        timeline.End = 3;

        timeline.Start.ShouldBe(3);
        timeline.End.ShouldBe(3);
    }

    [Fact]
    public void 时间轴_起点晚于终点时推着终点走()
    {
        var timeline = new PluginTimeline { Duration = 60, Start = 0, End = 10 };

        timeline.Start = 15;

        timeline.Start.ShouldBe(15);
        timeline.End.ShouldBe(15, "设置起点不应产生反向区间");
    }

    [Fact]
    public void 时间轴_时长为零按一秒处理()
    {
        var timeline = new PluginTimeline { Duration = 0, Start = 0.2, End = 0.8 };

        timeline.End = 5;

        timeline.End.ShouldBe(1);
        timeline.Start.ShouldBe(0.2);
    }

    [Fact]
    public void 时间轴_缩短时长会同时夹住起止()
    {
        var timeline = new PluginTimeline { Duration = 60, Start = 10, End = 40 };

        timeline.Duration = 20;

        timeline.End.ShouldBe(20);
        timeline.Start.ShouldBe(10);
    }

    [AvaloniaFact]
    public void 时间轴_外部设值后把手位置同步()
    {
        var timeline = new PluginTimeline { Duration = 60, Start = 0, End = 30 };
        var window = CreateWindow(timeline);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var width = timeline.Bounds.Width;
            width.ShouldBeGreaterThan(20);

            var usable = Math.Max(1, width - 10);
            var startHandle = Part(timeline, "PART_StartHandle");
            var endHandle = Part(timeline, "PART_EndHandle");

            Canvas.GetLeft(startHandle).ShouldBe(0, 0.5);
            Canvas.GetLeft(endHandle).ShouldBe(usable * 0.5, 0.5);

            timeline.Start = 15;

            Canvas.GetLeft(startHandle).ShouldBe(usable * 0.25, 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void 时间轴_拖动把手不越界也不反向()
    {
        var timeline = new PluginTimeline { Duration = 60, Start = 10, End = 30 };
        var window = CreateWindow(timeline);

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var usable = Math.Max(1, timeline.Bounds.Width - 10);
            var endX = 5 + usable * (30d / 60d);
            var origin = timeline.TranslatePoint(new Point(0, 0), window)!.Value;

            // 抓住右把手往左拖出轨道：应停在起点，而不是变成负区间。
            window.MouseDown(origin + new Point(endX, 16), MouseButton.Left);
            window.MouseMove(origin + new Point(0, 16));
            window.MouseUp(origin + new Point(0, 16), MouseButton.Left);

            timeline.Start.ShouldBe(10);
            timeline.End.ShouldBe(10);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateWindow(Control content)
    {
        var window = new Window
        {
            Width = 400,
            Height = 80,
            Content = content
        };

        // 模板与主题键由契约包提供；宿主真实运行时在 App.axaml 里统一引入。
        window.Styles.Add(new StyleInclude(new Uri("avares://Sparrow.Plugin.Ui/"))
        {
            Source = new Uri("avares://Sparrow.Plugin.Ui/Themes/PluginControls.axaml")
        });

        return window;
    }

    private static Border Part(PluginTimeline timeline, string name) =>
        timeline.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Name == name);
}
