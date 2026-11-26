// CaptureWindowTests 分部类：选区拖拽、边界约束与放大镜。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Shouldly;
using Sparrow.App.Services;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

public sealed partial class CaptureWindowTests
{
    /// <summary>拖出选区并松手：窗口不关，进入可编辑态。</summary>
    private static CaptureWindow CreateWithSelection()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        return window;
    }

    [AvaloniaFact]
    public void 松手后_选区保留且显示八个控制点()
    {
        var window = CreateWithSelection();

        window.IsVisible.ShouldBeTrue();
        window.ActionBar.IsVisible.ShouldBeTrue();
        window.HandleLayer.IsVisible.ShouldBeTrue();
        window.HandleLayer.Children.Count.ShouldBe(8);
        window.Selection.Width.ShouldBe(200, 0.01);
        window.Selection.Height.ShouldBe(150, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 框内拖动_整体移动且尺寸不变()
    {
        var window = CreateWithSelection();

        window.MouseDown(new Point(200, 175), MouseButton.Left);
        window.MouseMove(new Point(250, 205));
        window.MouseUp(new Point(250, 205), MouseButton.Left);

        Canvas.GetLeft(window.Selection).ShouldBe(150, 0.01);
        Canvas.GetTop(window.Selection).ShouldBe(130, 0.01);
        window.Selection.Width.ShouldBe(200, 0.01);
        window.Selection.Height.ShouldBe(150, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 拖右下角_左上角保持不动()
    {
        var window = CreateWithSelection();

        window.MouseDown(new Point(300, 250), MouseButton.Left);
        window.MouseMove(new Point(350, 300));
        window.MouseUp(new Point(350, 300), MouseButton.Left);

        Canvas.GetLeft(window.Selection).ShouldBe(100, 0.01);
        Canvas.GetTop(window.Selection).ShouldBe(100, 0.01);
        window.Selection.Width.ShouldBe(250, 0.01);
        window.Selection.Height.ShouldBe(200, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 拖左边_右边保持不动()
    {
        var window = CreateWithSelection();

        window.MouseDown(new Point(100, 175), MouseButton.Left);
        window.MouseMove(new Point(50, 175));
        window.MouseUp(new Point(50, 175), MouseButton.Left);

        Canvas.GetLeft(window.Selection).ShouldBe(50, 0.01);
        window.Selection.Width.ShouldBe(250, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 拖动不小于最小尺寸()
    {
        var window = CreateWithSelection();

        window.MouseDown(new Point(300, 250), MouseButton.Left);
        window.MouseMove(new Point(50, 50));
        window.MouseUp(new Point(50, 50), MouseButton.Left);

        window.Selection.Width.ShouldBe(4, 0.01);
        window.Selection.Height.ShouldBe(4, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 移动不超出窗口边界()
    {
        var window = CreateWithSelection();

        window.MouseDown(new Point(200, 175), MouseButton.Left);
        window.MouseMove(new Point(-500, -500));
        window.MouseUp(new Point(-500, -500), MouseButton.Left);

        Canvas.GetLeft(window.Selection).ShouldBe(0, 0.01);
        Canvas.GetTop(window.Selection).ShouldBe(0, 0.01);

        window.Close();
    }

    /// <summary>点在动作栏的空白处不能当成「重新拉框」。</summary>
    [AvaloniaFact]
    public void 点击动作栏空白_不会重置选区()
    {
        var window = CreateWithSelection();
        var left = Canvas.GetLeft(window.ActionBar);
        var top = Canvas.GetTop(window.ActionBar);

        window.MouseDown(new Point(left + 4, top + 4), MouseButton.Left);
        window.MouseUp(new Point(left + 4, top + 4), MouseButton.Left);

        window.Selection.Width.ShouldBe(200, 0.01);
        window.Selection.Height.ShouldBe(150, 0.01);

        window.Close();
    }

    /// <summary>选区之外压暗、选区内保持原图亮度：遮罩用 EvenOdd 的 GeometryGroup 挖洞。</summary>
    [AvaloniaFact]
    public void 选区遮罩_按选区挖洞只压暗外面()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        // 不用 CombinedGeometry.Exclude：Avalonia 下它的 Bounds 可能算成空，
        // Path 会整块不绘制（遮罩消失）。EvenOdd 的 GeometryGroup 稳定。
        var group = window.DimLayer.Data.ShouldBeOfType<GeometryGroup>();
        group.FillRule.ShouldBe(FillRule.EvenOdd);
        group.Children.Count.ShouldBe(2);

        var hole = group.Children[1].ShouldBeOfType<RectangleGeometry>();
        hole.Rect.ShouldBe(new Rect(
            Canvas.GetLeft(window.Selection),
            Canvas.GetTop(window.Selection),
            window.Selection.Width,
            window.Selection.Height));

        window.CrosshairLayer.IsVisible.ShouldBeFalse("已经有选区，不再画十字线");
        window.Close();
    }

    /// <summary>还没框选时，十字线跟随光标，帮助对齐内容。</summary>
    [AvaloniaFact]
    public void 未框选时_十字线跟随光标()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseMove(new Point(240, 160));

        window.CrosshairLayer.IsVisible.ShouldBeTrue();
        window.CrosshairV.StartPoint.X.ShouldBe(240);
        window.CrosshairV.EndPoint.Y.ShouldBe(window.Height);
        window.CrosshairH.StartPoint.Y.ShouldBe(160);
        window.CrosshairH.EndPoint.X.ShouldBe(window.Width);

        window.Close();
    }

    /// <summary>放大镜不能压在工具条上：右下不行就换另外三个角。</summary>
    [Fact]
    public void 放大镜位置_避开工具条和窗口边缘()
    {
        var bounds = new Size(1280, 760);
        var toolbar = new Rect(580, 520, 400, 140);

        var placed = CaptureWindow.PlaceMagnifier(new Point(700, 500), 132, bounds, toolbar);

        placed.Intersects(toolbar).ShouldBeFalse();
        placed.X.ShouldBeGreaterThanOrEqualTo(8);
        placed.Y.ShouldBeGreaterThanOrEqualTo(8);
        placed.Right.ShouldBeLessThanOrEqualTo(bounds.Width - 8);
        placed.Bottom.ShouldBeLessThanOrEqualTo(bounds.Height - 8);
    }
}
