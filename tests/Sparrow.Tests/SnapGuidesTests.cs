using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Shouldly;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>吸附参考线：纯计算 + 截图窗口里的实际表现。</summary>
public sealed class SnapGuidesTests
{
    [Fact]
    public void 阈值内吸附_阈值外不动()
    {
        var (inside, hit) = SnapGuides.Snap(4, [0, 100]);
        inside.ShouldBe(0);
        hit.ShouldBe(0);

        var (outside, none) = SnapGuides.Snap(20, [0, 100]);
        outside.ShouldBe(20);
        none.ShouldBeNull();
    }

    [Fact]
    public void 边缘吸附_取更近的一条边()
    {
        // 左边缘离 100 差 3，右边缘离 100 差 97 → 按左边算
        var (delta, guide) = SnapGuides.SnapEdges(97, 197, [100]);
        delta.ShouldBe(3);
        guide.ShouldBe(100);

        // 两边都远：不吸附
        var (none, noGuide) = SnapGuides.SnapEdges(20, 120, [100]);
        none.ShouldBe(0);
        noGuide.ShouldBeNull();
    }

    [Fact]
    public void 标注落点吸附到选区边框和居中()
    {
        var box = new Rect(100, 100, 200, 100);

        // x 离左边 3 → 贴到 0；y 离中线 10 → 超出 6 的阈值，不吸
        var (point, vertical, horizontal) = SnapGuides.SnapToBox(new Point(103, 160), box);
        point.X.ShouldBe(100);
        vertical.ShouldBe(100);
        point.Y.ShouldBe(160);
        horizontal.ShouldBeNull();
    }

    [AvaloniaFact]
    public void 截图窗口_拖动选区贴边时出现参考线且松手消失()
    {
        var window = new CaptureWindow();
        window.Show();

        // 先拉一个选区
        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        // 按住选区内部往左上拖，让左边缘落到 5（会被吸到 0）
        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseMove(new Point(105, 150));

        Canvas.GetLeft(window.Selection).ShouldBe(0);
        window.GuideLayer.IsVisible.ShouldBeTrue();
        window.GuideLayer.Children.OfType<Line>()
            .ShouldContain(line => Math.Abs(line.StartPoint.X) < 0.01);

        window.MouseUp(new Point(105, 150), MouseButton.Left);
        window.GuideLayer.IsVisible.ShouldBeFalse();
        window.GuideLayer.Children.Count.ShouldBe(0);
    }
}
