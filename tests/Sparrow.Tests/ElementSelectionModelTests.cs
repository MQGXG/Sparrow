using Avalonia;
using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 元素级智能选区的路径模型：默认选层、整窗模式、显式选层、细化一致性。
/// </summary>
public sealed class ElementSelectionModelTests
{
    private static readonly PixelRect Window = new(0, 0, 400, 300);

    private static UiElementCandidate Element(
        int x, int y, int width, int height, string kind = "按钮") =>
        new(new PixelRect(x, y, width, height), kind, kind);

    private static UiElementCandidate WindowCandidate() =>
        new(Window, "窗口", "窗口");

    private static List<UiElementCandidate> Path() =>
    [
        Element(40, 60, 80, 24),
        Element(20, 40, 160, 80, "工具栏"),
        Element(0, 0, 300, 200, "窗格"),
        WindowCandidate()
    ];

    [Fact]
    public void 默认元素模式_选路径最深的一层()
    {
        var model = new ElementSelectionModel();

        model.ApplyPath(Path(), Window, minimumSize: 2).ShouldBeTrue();

        model.Target.ShouldBe(ElementSelectionTarget.WindowSubElement);
        model.Index.ShouldBe(0);
        model.Current!.Bounds.ShouldBe(new PixelRect(40, 60, 80, 24));
    }

    [Fact]
    public void 整窗模式_始终选末项()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);

        model.ToggleTarget().ShouldBeTrue();

        model.Target.ShouldBe(ElementSelectionTarget.Window);
        model.Index.ShouldBe(model.Path.Count - 1);
        model.Current!.Bounds.ShouldBe(Window);

        // 整窗模式下显式选层也应被钳到末项
        model.SelectIndex(0).ShouldBeTrue();
        model.Index.ShouldBe(model.Path.Count - 1);

        model.ToggleTarget().ShouldBeTrue();
        model.Current!.Bounds.ShouldBe(new PixelRect(40, 60, 80, 24));
    }

    [Fact]
    public void 滚轮切层_按路径边界收紧与放大()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);

        model.Step(1).ShouldBeTrue();
        model.Index.ShouldBe(1);
        model.Step(1).ShouldBeTrue();
        model.Index.ShouldBe(2);
        model.Step(1).ShouldBeTrue();
        model.Index.ShouldBe(3);

        // 已到末项：继续放大保持不动
        model.Step(1).ShouldBeTrue();
        model.Index.ShouldBe(3);

        // 向下滚到最深后继续收紧也保持不动
        model.Step(-10).ShouldBeTrue();
        model.Index.ShouldBe(0);
    }

    [Fact]
    public void 整窗模式下滚轮不切层()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);
        model.ToggleTarget();

        model.Step(1).ShouldBeFalse();
        model.Index.ShouldBe(model.Path.Count - 1);
    }

    [Fact]
    public void 细化只接受旧路径的尾部扩展_并保留显式选中的层()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);

        // 用户显式选了工具栏
        model.SelectIndex(1).ShouldBeTrue();

        var refined = new List<UiElementCandidate>
        {
            Element(45, 62, 20, 20, "图片"),
            Element(50, 64, 10, 10)
        };
        refined.AddRange(Path());

        model.ApplyRefinement(refined, Window, minimumSize: 2).ShouldBeTrue();

        // 工具栏在新路径里仍是同一块矩形
        model.Current!.Bounds.ShouldBe(new PixelRect(20, 40, 160, 80));
        model.Path.Count.ShouldBe(refined.Count);
    }

    [Fact]
    public void 细化路径不是尾部扩展时拒绝()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);

        var unrelated = new List<UiElementCandidate>
        {
            Element(45, 62, 20, 20, "图片"),
            Element(100, 100, 30, 30, "文本"), // 与旧路径第一层不同
        };
        unrelated.AddRange(Path().Skip(1));

        model.ApplyRefinement(unrelated, Window, minimumSize: 2).ShouldBeFalse();
        model.Current!.Bounds.ShouldBe(new PixelRect(40, 60, 80, 24));
    }

    [Fact]
    public void 细化路径没有变深时拒绝()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);

        model.ApplyRefinement(Path(), Window, minimumSize: 2).ShouldBeFalse();
        model.ApplyRefinement(Path().Take(2).ToList(), Window, minimumSize: 2).ShouldBeFalse();
    }

    [Fact]
    public void 路径裁剪到窗口_去重并丢掉过小的层()
    {
        var model = new ElementSelectionModel();
        var path = new List<UiElementCandidate>
        {
            Element(5, 5, 1, 1),          // 小于最小尺寸
            Element(5, 5, 10, 10),
            Element(5, 5, 10, 10),        // 重复
            Element(-10, -10, 50, 50),    // 裁到 (0,0,40,40)
            WindowCandidate()
        };

        model.ApplyPath(path, Window, minimumSize: 2).ShouldBeTrue();

        model.Path.Count.ShouldBe(3);
        model.Path[0].Bounds.ShouldBe(new PixelRect(5, 5, 10, 10));
        model.Path[1].Bounds.ShouldBe(new PixelRect(0, 0, 40, 40));
        model.Path[2].Bounds.ShouldBe(Window);
    }

    [Fact]
    public void 相同路径不会重置用户已选的层()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);
        model.Step(2).ShouldBeTrue();

        model.ApplyPath(Path(), Window, minimumSize: 2).ShouldBeFalse();
        model.Index.ShouldBe(2);
    }

    [Fact]
    public void 粗探继续收紧时_保留显式选中的层()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);
        model.SelectIndex(1).ShouldBeTrue();

        var deeper = new List<UiElementCandidate>
        {
            Element(45, 62, 20, 20, "图片")
        };
        deeper.AddRange(Path());

        model.ApplyPath(deeper, Window, minimumSize: 2).ShouldBeTrue();

        model.Current!.Bounds.ShouldBe(new PixelRect(20, 40, 160, 80));
    }

    [Fact]
    public void 细化路径中间插入新层时接受()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);
        model.SelectIndex(0).ShouldBeTrue();

        var refined = new List<UiElementCandidate>
        {
            Element(45, 62, 20, 20, "图片"),   // 更深的新元素
            Element(40, 60, 80, 24),           // 旧的最深项
            Element(10, 30, 180, 100, "面板")  // 旧层之间新插入
        };
        refined.AddRange(Path().Skip(1));

        model.ApplyRefinement(refined, Window, minimumSize: 2).ShouldBeTrue();
        model.Current!.Bounds.ShouldBe(new PixelRect(40, 60, 80, 24));
    }

    [Fact]
    public void 路径完全变化时_显式选层失效()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);
        model.SelectIndex(1).ShouldBeTrue();

        var other = new List<UiElementCandidate>
        {
            Element(200, 200, 40, 20, "链接"),
            WindowCandidate()
        };

        model.ApplyPath(other, Window, minimumSize: 2).ShouldBeTrue();

        model.Index.ShouldBe(0);
        model.Current!.ControlType.ShouldBe("链接");
    }

    [Fact]
    public void 子窗口切换_允许同窗更深路径替换()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(
            [Element(0, 0, 300, 200, "文档"), WindowCandidate()],
            Window,
            minimumSize: 2);

        var childPath = new List<UiElementCandidate>
        {
            Element(10, 10, 50, 20, "链接"),
            Element(0, 0, 300, 200, "客户端"),
            WindowCandidate()
        };

        model.CanReplaceWith(childPath, Window, minimumSize: 2).ShouldBeTrue();
    }

    [Fact]
    public void 显式选层后_兜底路径不能替换()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(
            [Element(0, 0, 300, 200, "文档"), WindowCandidate()],
            Window,
            minimumSize: 2);
        model.SelectIndex(0).ShouldBeTrue();

        var childPath = new List<UiElementCandidate>
        {
            Element(10, 10, 50, 20, "链接"),
            WindowCandidate()
        };

        model.CanReplaceWith(childPath, Window, minimumSize: 2).ShouldBeFalse();
    }

    [Fact]
    public void 不同窗口的路径不能替换()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(
            [Element(0, 0, 300, 200, "文档"), WindowCandidate()],
            Window,
            minimumSize: 2);

        var otherWindow = new List<UiElementCandidate>
        {
            Element(10, 10, 50, 20, "链接"),
            Element(0, 0, 200, 150, "窗口")
        };

        model.CanReplaceWith(otherWindow, Window, minimumSize: 2).ShouldBeFalse();
    }

    [Fact]
    public void 浏览器真实路径_选中最深链接而不是窗口()
    {
        var candidateBounds = new PixelRect(941, 461, 1619, 979);

        var path = new List<UiElementCandidate>
        {
            Element(1214, 811, 311, 19, "链接"),
            Element(1198, 800, 406, 41, "数据项"),
            Element(837, 800, 903, 41, "数据项"),
            Element(836, 597, 905, 488, "表格"),
            Element(632, 403, 1625, 921, "分组"),
            Element(632, 387, 1625, 937, "分组"),
            Element(632, 387, 1640, 937, "窗口"),
            new(new PixelRect(621, 307, 1662, 1028), "窗口", "窗口")
        };

        var model = new ElementSelectionModel();
        model.ApplyPath(path, candidateBounds, minimumSize: 4).ShouldBeTrue();

        model.Current!.ControlType.ShouldBe("链接");
        model.Current.Bounds.ShouldBe(new PixelRect(1214, 811, 311, 19));
        model.Path[^1].ControlType.ShouldBe("窗口");
    }

    [Fact]
    public void 浏览器路径从窗口细化后仍然选中链接()
    {
        var candidateBounds = new PixelRect(941, 461, 1619, 979);
        var windowOnly = new List<UiElementCandidate>
        {
            new(new PixelRect(621, 307, 1662, 1028), "窗口", "窗口")
        };

        var refined = new List<UiElementCandidate>
        {
            Element(1214, 811, 311, 19, "链接"),
            Element(837, 800, 903, 41, "数据项"),
            Element(836, 597, 905, 488, "表格"),
            Element(632, 387, 1640, 937, "窗口"),
            new(new PixelRect(621, 307, 1662, 1028), "窗口", "窗口")
        };

        var model = new ElementSelectionModel();
        model.ApplyPath(windowOnly, candidateBounds, minimumSize: 4).ShouldBeTrue();
        model.ApplyRefinement(refined, candidateBounds, minimumSize: 4).ShouldBeTrue();

        model.Current!.ControlType.ShouldBe("链接");
    }

    [Fact]
    public void 空路径清空当前层()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);

        model.ApplyPath([], Window, minimumSize: 2).ShouldBeTrue();

        model.HasSelection.ShouldBeFalse();
        model.Current.ShouldBeNull();
        model.Window.ShouldBeNull();
    }

    [Fact]
    public void 窗口始终是路径末项()
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(Path(), Window, minimumSize: 2);

        model.Window!.Bounds.ShouldBe(Window);
        model.Window.ControlType.ShouldBe("窗口");
    }
}
