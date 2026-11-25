// AnnotationTests 分部类：序号、文字与水印。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Shouldly;
using SkiaSharp;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

public sealed partial class AnnotationTests
{
    [AvaloniaFact]
    public void 序号_外形直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.SelectNote(0);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Shape.ShouldBe(NumberShape.SolidCircle);

        // 实心圆预览 = 圆 + 文字（2 个子元素）
        var circleChildren = window.NoteLayer.Children.Count;

        window.SetNumberShape(NumberShape.Pin);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Shape.ShouldBe(NumberShape.Pin);
        window.NumberShapeButton.Content.ShouldBe("定位针");

        // 定位针预览 = 圆头 + 尖三角 + 文字（3 个子元素）
        window.NoteLayer.Children.Count.ShouldBeGreaterThan(circleChildren);

        window.SetNumberShape(NumberShape.Hexagon);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Shape.ShouldBe(NumberShape.Hexagon);
        window.NumberShapeButton.Content.ShouldBe("六边形");

        window.Close();
    }

    [Fact]
    public void 序号组合_详情区按目标点推导_显式几何优先()
    {
        var note = new NumberAnnotation(new Point(0, 0), 1, 0xFFFF0000, 24,
            Combo: NumberCombo.DashedBox, Target: new Point(100, 50));

        // 详情区 = badge 中心 → 目标点的包围盒
        var badge = NumberGeometry.Bounds(note);
        var detail = NumberGeometry.DetailBounds(note);
        detail.Left.ShouldBe(badge.Center.X);
        detail.Top.ShouldBe(badge.Center.Y);
        detail.Right.ShouldBe(100);
        detail.Bottom.ShouldBe(50);

        // 拖过之后用显式几何，不再推导
        var fixedBox = new Rect(10, 20, 30, 40);
        NumberGeometry.DetailBounds(note with { Detail = fixedBox }).ShouldBe(fixedBox);
    }

    [AvaloniaFact]
    public void 序号_组合详情区直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.SelectNote(0);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Combo.ShouldBe(NumberCombo.Plain);

        window.SetNumberCombo(NumberCombo.DashedBox);
        var dashed = window.Notes[0].ShouldBeOfType<NumberAnnotation>();
        dashed.Combo.ShouldBe(NumberCombo.DashedBox);
        window.NumberComboButton.Content.ShouldBe("虚线框");

        // 详情区从徽章中心往右下撑开
        NumberGeometry.DetailBounds(dashed).Right
            .ShouldBeGreaterThan(NumberGeometry.Bounds(dashed).Center.X);

        window.SetNumberCombo(NumberCombo.Highlight);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Combo.ShouldBe(NumberCombo.Highlight);
        window.NumberComboButton.Content.ShouldBe("高亮区域");

        window.SetNumberCombo(NumberCombo.Plain);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Combo.ShouldBe(NumberCombo.Plain);
        window.NumberComboButton.Content.ShouldBe("仅序号");

        window.Close();
    }

    [AvaloniaFact]
    public void 序号_画完不选中也能直接换外形()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        // 故意不调用 SelectNote：模拟「刚画完直接改外形」
        window.SetNumberShape(NumberShape.Capsule);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Shape.ShouldBe(NumberShape.Capsule);

        window.SetNumberFormat(NumberFormat.Chinese);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Format.ShouldBe(NumberFormat.Chinese);

        window.Close();
    }

    [Fact]
    public void 序号组合_引导线四点()
    {
        var note = new NumberAnnotation(new Point(0, 0), 1, 0xFFFF0000, 24,
            Combo: NumberCombo.Leader, Target: new Point(120, 60));

        var points = NumberGeometry.LeaderPoints(note);
        points.Count.ShouldBe(4);

        // badge → 近肘（水平）→ 远肘（目标高度）→ 目标
        points[1].Y.ShouldBe(points[0].Y, 0.01);
        points[2].Y.ShouldBe(60, 0.01);
        points[3].ShouldBe(new Point(120, 60));
    }

    [AvaloniaFact]
    public void 序号_拖拽创建时目标点跟随指针()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // 按下放 badge，拖到右下定点，松手提交
        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        var note = window.Notes[0].ShouldBeOfType<NumberAnnotation>();

        // 选区原点是 (100,100)：徽章落在 (80,80)，目标点落在 (200,150)
        note.Center.ShouldBe(new Point(80, 80));
        note.Target.ShouldNotBeNull();
        note.Target!.Value.X.ShouldBe(200, 0.5);
        note.Target!.Value.Y.ShouldBe(150, 0.5);
        note.Number.ShouldBe(1);

        window.Close();
    }

    [Fact]
    public void 序号组合_文本标签与放大说明的详情区()
    {
        var target = new Point(200, 100);

        var text = new NumberAnnotation(new Point(0, 0), 1, 0xFFFF0000, 24,
            Combo: NumberCombo.Text, Target: target);

        var textBox = NumberGeometry.DetailBounds(text);
        textBox.Left.ShouldBeGreaterThan(NumberGeometry.Bounds(text).Right);   // 贴在徽章右侧

        var magnify = text with { Combo = NumberCombo.Magnify };
        var lens = NumberGeometry.DetailBounds(magnify);
        lens.Center.X.ShouldBe(target.X, 0.01);
        lens.Center.Y.ShouldBe(target.Y, 0.01);
        lens.Width.ShouldBe(24 * 2.5, 0.01);   // 半径 size*1.25
    }

    [AvaloniaFact]
    public void 序号_文本标签说明直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.SelectNote(0);
        window.SetNumberCombo(NumberCombo.Text);
        window.NumberTextGroup.IsVisible.ShouldBeTrue();

        window.NumberTextBox.Text = "注意这里";
        window.CommitNumberText();

        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Text.ShouldBe("注意这里");

        window.Close();
    }

    [Fact]
    public void 序号徽章_自动数字保持正圆_只有标签才撑宽()
    {
        var auto = new NumberAnnotation(new Point(0, 0), 8, 0xFFFF0000, 24);

        var badge = NumberGeometry.Bounds(auto);
        badge.Width.ShouldBe(24, 0.01);
        badge.Height.ShouldBe(24, 0.01);

        var labeled = auto with { Label = "第一步" };
        NumberGeometry.Bounds(labeled).Width.ShouldBeGreaterThan(24);
        NumberGeometry.Bounds(labeled).Height.ShouldBe(24, 0.01);
    }

    [AvaloniaFact]
    public void 序号_文本字号与放大倍率直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.SelectNote(0);

        window.SetNumberCombo(NumberCombo.Text);
        window.SetNumberTextSize(24);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().TextSize.ShouldBe(24);
        window.NumberTextSizeButton.Content.ShouldBe("24 px");

        window.SetNumberCombo(NumberCombo.Magnify);
        window.NumberZoomGroup.IsVisible.ShouldBeTrue();
        window.SetNumberZoom(4);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Zoom.ShouldBe(4);
        window.NumberZoomButton.Content.ShouldBe("4×");

        window.Close();
    }

    [AvaloniaFact]
    public void 序号_文本组合画完自动进入说明编辑()
    {
        var window = CreateWindowWithSelection();
        window.SetNumberCombo(NumberCombo.Text);
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        // 画完直接进入就地编辑
        window.TextNoteBox.IsVisible.ShouldBeTrue();

        window.TextNoteBox.Text = "注意";
        window.CommitTextNote();

        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Text.ShouldBe("注意");

        window.Close();
    }

    [AvaloniaFact]
    public void 序号_拖目标点把手改变详情区()
    {
        var window = CreateWindowWithSelection();
        window.SetNumberCombo(NumberCombo.DashedBox);
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        window.Notes[0].ShouldBeOfType<NumberAnnotation>()
            .Target!.Value.X.ShouldBe(200, 0.5);   // 选区原点 (100,100)

        // 切到指针工具，选中后拖目标点（窗口坐标 300,250 = 局部 200,150）
        window.SelectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.SelectNote(0);

        window.MouseDown(new Point(300, 250), MouseButton.Left);
        window.MouseMove(new Point(360, 280));
        window.MouseUp(new Point(360, 280), MouseButton.Left);

        var moved = window.Notes[0].ShouldBeOfType<NumberAnnotation>();
        moved.Target!.Value.X.ShouldBe(260, 0.5);
        moved.Target!.Value.Y.ShouldBe(180, 0.5);

        window.Close();
    }

    [AvaloniaFact]
    public void 调色板_取色光标从左上角定位()
    {
        var window = new CaptureWindow();
        window.Show();

        window.CustomColorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.CustomColorPanel.IsVisible.ShouldBeTrue();

        // Margin 是相对左上角的偏移；如果对齐是默认 Stretch，光标会从居中位置再偏移，和鼠标对不上
        window.SvCursor.HorizontalAlignment.ShouldBe(Avalonia.Layout.HorizontalAlignment.Left);
        window.SvCursor.VerticalAlignment.ShouldBe(Avalonia.Layout.VerticalAlignment.Top);

        window.Close();
    }

    [AvaloniaFact]
    public void 手绘描边_实时预览也画抖动路径()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(50, 50), MouseButton.Left);
        window.MouseMove(new Point(400, 300));
        window.MouseUp(new Point(400, 300), MouseButton.Left);

        // 先选「手绘」，再画矩形
        window.SetLineStroke(LineDash.Solid, LineStyle.Sketch);
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<ShapeAnnotation>().Style.ShouldBe(LineStyle.Sketch);

        // 预览层应该是一条手绘 Path，而不是规整的 Rectangle
        window.NoteLayer.Children.Any(child => child is Avalonia.Controls.Shapes.Path)
            .ShouldBeTrue();
        window.NoteLayer.Children.Any(child => child is Avalonia.Controls.Shapes.Rectangle)
            .ShouldBeFalse();

        window.Close();
    }

    [Fact]
    public void 序号外形_顶点与文字锚点()
    {
        var bounds = new Rect(0, 0, 40, 40);

        // 圆 / 方族没有顶点，各自有画法
        NumberGeometry.Polygon(NumberShape.SolidCircle, bounds).ShouldBeNull();
        NumberGeometry.Polygon(NumberShape.Capsule, bounds).ShouldBeNull();

        // 六边形尖顶朝上
        var hexagon = NumberGeometry.Polygon(NumberShape.Hexagon, bounds)!;
        hexagon.Count.ShouldBe(6);
        hexagon[0].X.ShouldBe(20, 0.01);
        hexagon[0].Y.ShouldBe(0, 0.01);

        // 旗标 5 点、底边中央有 V 形缺口；角标是右上三角
        var flag = NumberGeometry.Polygon(NumberShape.Flag, bounds)!;
        flag.Count.ShouldBe(5);
        flag[3].Y.ShouldBe(30, 0.01);   // bottom - r*0.5 = 40 - 20*0.5

        NumberGeometry.Polygon(NumberShape.Corner, bounds)!.Count.ShouldBe(3);

        // 胶囊更宽、定位针更高
        NumberGeometry.BadgeBounds(new Point(0, 0), 32, NumberShape.Capsule).Width.ShouldBe(48, 0.01);
        NumberGeometry.BadgeBounds(new Point(0, 0), 32, NumberShape.Pin).Height.ShouldBe(41.6, 0.01);

        // 定位针的文字在圆头里；角标文字靠右上
        var pin = NumberGeometry.BadgeBounds(new Point(0, 0), 32, NumberShape.Pin);
        NumberGeometry.TextAnchor(pin, NumberShape.Pin).Y.ShouldBe(pin.Top + 16, 0.01);
        NumberGeometry.TextAnchor(bounds, NumberShape.Corner).X
            .ShouldBeGreaterThan(bounds.Center.X);
    }

    [Fact]
    public void 形状标注_斜线与交叉斜线填充换用独立填充色()
    {
        var png = CreateWhitePng(120, 120);

        ShapeAnnotation Make(ShapeFill fill) => new(
            new Rect(10, 10, 100, 100),
            0xFFFF0000,   // 红色描边
            2,
            false,
            Opacity: 1,
            Fill: fill,
            FillColor: 0xFF0000FF);   // 蓝色填充

        int BluePixels(SKBitmap bitmap)
        {
            var count = 0;
            for (var x = 20; x < 100; x++)
            {
                for (var y = 20; y < 100; y++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.Blue > 180 && pixel.Red < 140)
                        count++;
                }
            }

            return count;
        }

        using var line = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [Make(ShapeFill.Line)], 1));
        using var cross = SKBitmap.Decode(
            AnnotationRenderer.Compose(png, [Make(ShapeFill.CrossLine)], 1));

        var linePixels = BluePixels(line);
        linePixels.ShouldBeGreaterThan(0);

        // 交叉斜线比单向斜线多一组线，蓝色像素更多
        BluePixels(cross).ShouldBeGreaterThan(linePixels);
    }

    [AvaloniaFact]
    public void 序号_类型与手动序号直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.NumberToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(180, 180), MouseButton.Left);
        window.MouseUp(new Point(180, 180), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Number.ShouldBe(1);

        window.SelectNote(0);

        // 数字类型：阿拉伯 → 罗马
        window.SetNumberFormat(NumberFormat.Roman);
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Format.ShouldBe(NumberFormat.Roman);

        // 手动序号：改成 7，显示也跟着变
        window.NumberBox.Text = "7";
        window.CommitNumberBox();
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Number.ShouldBe(7);
        window.NumberBox.Text.ShouldBe("7");

        // 标签：留空显示序号，填了就覆盖
        window.LabelBox.Text = "第一步";
        window.CommitLabelBox();
        window.Notes[0].ShouldBeOfType<NumberAnnotation>().Label.ShouldBe("第一步");

        window.Close();
    }

    [AvaloniaFact]
    public void 水印_属性栏文本直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.WatermarkToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseUp(new Point(200, 200), MouseButton.Left);

        window.Notes.Count.ShouldBe(1);
        window.Notes[0].ShouldBeOfType<WatermarkAnnotation>().Text.ShouldBe("Sparrow");

        window.SelectNote(0);
        window.WatermarkBox.Text = "机密文件";
        window.CommitWatermarkBox();
        window.Notes[0].ShouldBeOfType<WatermarkAnnotation>().Text.ShouldBe("机密文件");

        window.Close();
    }

    [AvaloniaFact]
    public void 文字_字体对齐背景描边直接作用于选中标注()
    {
        var window = CreateWindowWithSelection();
        window.TextToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(170, 170), MouseButton.Left);
        window.MouseUp(new Point(170, 170), MouseButton.Left);

        window.TextNoteBox.Text = "Hello";
        window.CommitTextNote();
        window.Notes.Count.ShouldBe(1);

        window.SelectNote(0);

        window.SetTextAlign(TextAlign.Center);
        window.Notes[0].ShouldBeOfType<TextAnnotation>().Align.ShouldBe(TextAlign.Center);

        window.SetTextBackground(0xFF000000);
        window.Notes[0].ShouldBeOfType<TextAnnotation>().BackgroundColor.ShouldBe(0xFF000000u);

        window.SetTextStrokeWidth(2);
        window.Notes[0].ShouldBeOfType<TextAnnotation>().StrokeWidth.ShouldBe(2);

        window.SetTextStrokeColor(0xFFFFFFFF);
        window.Notes[0].ShouldBeOfType<TextAnnotation>().StrokeColor.ShouldBe(0xFFFFFFFFu);

        window.ItalicButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Notes[0].ShouldBeOfType<TextAnnotation>().Italic.ShouldBeTrue();

        window.Close();
    }
}
