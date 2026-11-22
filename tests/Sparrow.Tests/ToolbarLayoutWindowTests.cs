using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Shouldly;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>工具条布局落到真实按钮：隐藏、入口、顺序、全隐藏和损坏回落。</summary>
public sealed class ToolbarLayoutWindowTests
{
    [AvaloniaFact]
    public void 工具条_默认布局有四个折叠组和四个独立按钮()
    {
        var window = new CaptureWindow();
        window.Show();

        window.ToolButtons.Children.Count.ShouldBe(8);
        window.ToolButtons.Children[0].ShouldBe(window.ShapeGroupButton);
        window.ShapeGroupButton.IsVisible.ShouldBeTrue();
        window.RectToolButton.IsVisible.ShouldBeFalse();

        window.Close();
    }

    [AvaloniaFact]
    public void 工具条_隐藏折叠组成员后只剩独立按钮()
    {
        var window = new CaptureWindow();
        window.CaptureToolHidden = ["Ellipse", "Bracket"];
        window.Show();

        window.ShapeGroupButton.IsVisible.ShouldBeFalse();
        window.RectToolButton.IsVisible.ShouldBeTrue();
        window.ToolButtons.Children.ShouldContain(window.RectToolButton);

        window.CaptureToolHidden.ShouldContain("Ellipse");
        window.CaptureToolHidden.ShouldContain("Bracket");

        window.Close();
    }

    [AvaloniaFact]
    public void 工具条_设为入口后折叠按钮图标和点击目标跟着变()
    {
        var window = new CaptureWindow();
        window.Show();

        window.SetToolEntry(AnnotationTool.Ellipse);

        ReferenceEquals(window.ShapeGroupIcon.Data, window.FindResource("ToolEllipse"))
            .ShouldBeTrue();

        window.ShapeGroupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.EllipseToolButton.Classes.Contains("selected").ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 工具条_全部工具隐藏后指针撤销完成仍在()
    {
        var window = new CaptureWindow();
        window.CaptureToolHidden = Enum.GetValues<AnnotationTool>()
            .Where(tool => tool != AnnotationTool.None)
            .Select(tool => tool.ToString())
            .ToList();
        window.Show();

        window.ToolButtons.Children.Count.ShouldBe(0);
        window.SelectToolButton.IsVisible.ShouldBeTrue();
        window.UndoButton.IsVisible.ShouldBeTrue();
        window.CancelButton.IsVisible.ShouldBeTrue();
        window.CopyImageButton.IsVisible.ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 工具条_损坏布局回落默认()
    {
        var window = new CaptureWindow();
        window.CaptureToolPositions = ["???", "not-a-tool"];
        window.Show();

        window.CaptureToolPositions[0].ShouldBe("Ellipse|Bracket|Rectangle");
        window.ToolButtons.Children.Count.ShouldBe(8);
        window.ShapeGroupButton.IsVisible.ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 工具条_分组顺序改变后按钮顺序跟着变()
    {
        var window = new CaptureWindow();
        var positions = window.CaptureToolPositions.ToList();
        positions.Remove("Eraser");
        positions.Insert(0, "Eraser");
        window.CaptureToolPositions = positions;
        window.Show();

        window.ToolButtons.Children[0].ShouldBe(window.EraserToolButton);
        window.ToolButtons.Children[1].ShouldBe(window.ShapeGroupButton);

        window.Close();
    }
}
