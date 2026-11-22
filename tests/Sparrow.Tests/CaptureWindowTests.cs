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

/// <summary>
/// 截图动作栏的键盘路由。
/// 和面板一样用 headless 验证：真机 keybd_event 对字母键不稳定，
/// 不能作为「快捷键起作用」的证据。
/// </summary>
public sealed class CaptureWindowTests
{
    private static CaptureWindow CreateWithActionBar()
    {
        var window = new CaptureWindow();
        window.Show();
        window.ActionBar.IsVisible = true;
        return window;
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter, CaptureAction.Recognize)]
    [InlineData(Key.R, CaptureAction.Recognize)]
    [InlineData(Key.C, CaptureAction.CopyImage)]
    [InlineData(Key.S, CaptureAction.Save)]
    [InlineData(Key.P, CaptureAction.Pin)]
    [InlineData(Key.G, CaptureAction.Scroll)]
    public void 动作栏快捷键_选中对应动作并关闭窗口(Key key, CaptureAction expected)
    {
        var window = CreateWithActionBar();

        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

        window.Action.ShouldBe(expected);
        window.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Esc_取消并关闭窗口()
    {
        var window = CreateWithActionBar();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        window.Action.ShouldBe(CaptureAction.None);
        window.IsVisible.ShouldBeFalse();
    }

    /// <summary>还没截完图时，字母键不应该提前触发动作。</summary>
    [AvaloniaFact]
    public void 动作栏未出现时_快捷键不生效()
    {
        var window = new CaptureWindow();
        window.Show();

        window.KeyPress(Key.P, RawInputModifiers.None, PhysicalKey.None, null);

        window.Action.ShouldBe(CaptureAction.None);
        window.IsVisible.ShouldBeTrue();

        window.Close();
    }

    /// <summary>拖拽过程中要实时显示选区尺寸（物理像素）。</summary>
    [AvaloniaFact]
    public void 拖拽时显示选区尺寸()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));

        window.SizeBadge.IsVisible.ShouldBeTrue();
        window.WidthBox.Text.ShouldNotBeNullOrEmpty();
        window.HeightBox.Text.ShouldNotBeNullOrEmpty();

        window.Close();
    }

    /// <summary>中文输入法下字母键是 ImeProcessed，要按物理键还原，快捷键不能失效。</summary>
    [AvaloniaTheory]
    [InlineData(PhysicalKey.Enter, CaptureAction.Recognize)]
    [InlineData(PhysicalKey.C, CaptureAction.CopyImage)]
    [InlineData(PhysicalKey.S, CaptureAction.Save)]
    [InlineData(PhysicalKey.P, CaptureAction.Pin)]
    [InlineData(PhysicalKey.G, CaptureAction.Scroll)]
    public void 输入法处理过的按键_仍按物理键触发动作(PhysicalKey physicalKey, CaptureAction expected)
    {
        var window = CreateWithActionBar();

        window.KeyPress(Key.ImeProcessed, RawInputModifiers.None, physicalKey, null);

        window.Action.ShouldBe(expected);
        window.IsVisible.ShouldBeFalse();
    }

    /// <summary>就地 OCR：每行一个框，叠在选区上。</summary>
    [AvaloniaFact]
    public void 识别结果_按行框叠加()
    {
        var window = new CaptureWindow();
        window.Show();
        window.Selection.IsVisible = true;
        window.Selection.Width = 200;
        window.Selection.Height = 100;

        window.ShowOcrResult(new CaptureOcrResult(
            "HELLO OCR\n第二行",
            [
                new OcrTextLine("HELLO OCR", 10, 20, 90, 20),
                new OcrTextLine("第二行", 0, 60, 60, 20)
            ]));

        window.OcrLayer.IsVisible.ShouldBeTrue();
        window.OcrLayer.Children.Count.ShouldBe(2);
        window.CopyAllButton.IsVisible.ShouldBeTrue();
        window.OcrStatus.Text.ShouldNotBeNull().ShouldContain("2 行");

        window.Close();
    }

    /// <summary>识别框三档：全部显示 / 悬停隐藏 / 关闭。</summary>
    [AvaloniaFact]
    public void 识别框模式_切换后框可见性变化()
    {
        var window = new CaptureWindow { CaptureOcrBoxMode = OcrBoxMode.Hover };
        window.Show();
        window.Selection.IsVisible = true;
        window.Selection.Width = 200;
        window.Selection.Height = 100;

        window.ShowOcrResult(new CaptureOcrResult(
            "A\nB",
            [
                new OcrTextLine("A", 0, 0, 10, 10),
                new OcrTextLine("B", 0, 20, 10, 10)
            ]));

        var boxes = window.OcrLayer.Children.OfType<Avalonia.Controls.Shapes.Rectangle>().ToList();
        boxes.Count.ShouldBe(2);
        boxes.ShouldAllBe(box => !box.IsVisible);
        window.OcrLayer.IsHitTestVisible.ShouldBeTrue();

        window.CaptureOcrBoxMode = OcrBoxMode.All;
        boxes.ShouldAllBe(box => box.IsVisible);

        window.CaptureOcrBoxMode = OcrBoxMode.Off;
        boxes.ShouldAllBe(box => !box.IsVisible);
        window.OcrLayer.IsHitTestVisible.ShouldBeFalse();

        window.Close();
    }

    /// <summary>有识别结果时，Esc 先清 overlay，不直接关窗。</summary>
    [AvaloniaFact]
    public void 有识别结果时_Esc先清除overlay()
    {
        var window = new CaptureWindow();
        window.Show();
        window.Selection.Width = 100;
        window.Selection.Height = 50;

        window.ShowOcrResult(new CaptureOcrResult(
            "A",
            [new OcrTextLine("A", 0, 0, 10, 10)]));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        window.OcrLayer.IsVisible.ShouldBeFalse();
        window.IsVisible.ShouldBeTrue();

        window.Close();
    }

    [Fact]
    public void 工具条_上方被尺寸浮条挡住时叠到浮条上方()
    {
        // 选区贴底：下方放不下，上方又被尺寸浮条占住
        var selection = new Rect(100, 880, 300, 100);
        var badge = new Rect(100, 850, 200, 22);

        var position = CaptureWindow.ComputeToolbarPosition(
            selection, new Size(400, 40), new Size(1280, 1000), badge);

        position.Y.ShouldBe(850 - 40 - 8);
        position.X.ShouldBe(50);
    }

    [Fact]
    public void 工具条_多屏边界按当前显示器钳制()
    {
        // 左屏 1000..1800，选区贴右边缘：不能跨到窗口的虚拟桌面边界
        var position = CaptureWindow.ComputeToolbarPosition(
            new Rect(1700, 100, 80, 50),
            new Size(400, 40),
            new Rect(1000, 0, 800, 600));

        position.X.ShouldBe(1800 - 400 - 8);
    }

    [Fact]
    public void 工具条_下方放不下时翻到选区上方()
    {
        var selection = new Rect(100, 900, 300, 100);
        var position = CaptureWindow.ComputeToolbarPosition(
            selection, new Size(400, 40), new Size(1280, 1000));

        position.Y.ShouldBe(900 - 40 - 8);
        position.X.ShouldBe(50);   // 居中到选区中点（100 + 300/2 - 400/2）
    }

    [Fact]
    public void 工具条_贴边时钳制在窗口内()
    {
        // 选区在最左侧，工具条比选区宽：不能跑到窗口外
        var position = CaptureWindow.ComputeToolbarPosition(
            new Rect(0, 100, 50, 50), new Size(400, 40), new Size(1280, 1000));

        position.X.ShouldBe(8);
        position.Y.ShouldBe(158);
    }

    [Fact]
    public void 结果卡片_优先右侧_放不下换左侧()
    {
        var right = CaptureWindow.ComputeCardPosition(
            new Rect(100, 200, 300, 150), new Size(320, 220), new Size(1280, 800));
        right.X.ShouldBe(412);
        right.Y.ShouldBe(200);

        var left = CaptureWindow.ComputeCardPosition(
            new Rect(900, 200, 300, 150), new Size(320, 220), new Size(1280, 800));
        left.X.ShouldBe(900 - 320 - 12);
    }

    [AvaloniaFact]
    public void 截图工具条_按语言列表生成翻译菜单()
    {
        var window = new CaptureWindow();
        window.ConfigureTranslateTargets([("中文", "中文"), ("English", "英语")]);

        window.TranslateButton.IsVisible.ShouldBeTrue();

        var menu = window.TranslateButton.Flyout.ShouldBeOfType<MenuFlyout>();
        menu.Items.Count.ShouldBe(2);

        // 没有语言声明时整个按钮隐藏，避免点了没反应
        window.ConfigureTranslateTargets([]);
        window.TranslateButton.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void 结果卡片_显示后贴着选区并留在窗口内()
    {
        var window = CreateWithSelection();

        window.ShowResultCard("原文\n\n—— 译文（English）——\n\nTranslated");

        window.ResultCard.IsVisible.ShouldBeTrue();
        window.ResultText.Text.ShouldNotBeNull().ShouldContain("Translated");

        var left = Canvas.GetLeft(window.ResultCard);
        var top = Canvas.GetTop(window.ResultCard);
        left.ShouldBeGreaterThanOrEqualTo(8);
        top.ShouldBeGreaterThanOrEqualTo(8);
    }

    [AvaloniaFact]
    public void 拖动把手_工具条改变位置_选区变动后恢复跟随()
    {
        var window = CreateWithSelection();
        var before = Canvas.GetLeft(window.ActionBar);
        double.IsNaN(before).ShouldBeFalse();

        var handle = window.ToolbarDragHandle
            .TranslatePoint(new Point(6, 10), window)!.Value;

        window.MouseDown(handle, MouseButton.Left);
        window.MouseMove(new Point(handle.X + 120, handle.Y + 90));
        window.MouseUp(new Point(handle.X + 120, handle.Y + 90), MouseButton.Left);

        var moved = Canvas.GetLeft(window.ActionBar);
        moved.ShouldNotBe(before);

        // 选区一动，工具条重新贴回选区中线（参考 resetPositionForSelection：
        // 手动拖开只影响「内容变化」时的重排，不影响选区驱动的重新锚定）
        window.MouseDown(new Point(200, 200), MouseButton.Left);
        window.MouseMove(new Point(260, 240));
        window.MouseUp(new Point(260, 240), MouseButton.Left);

        Canvas.GetLeft(window.ActionBar).ShouldNotBe(moved);

        // 位置应等于「按当前选区重新锚定」的结果（窗口窄时会钳到左边缘，所以不能断言居中）
        var selectionRect = new Rect(
            Canvas.GetLeft(window.Selection), Canvas.GetTop(window.Selection),
            window.Selection.Width, window.Selection.Height);
        var expected = CaptureWindow.ComputeToolbarPosition(
            selectionRect, window.ActionBar.Bounds.Size, new Size(window.Width, window.Height));
        Canvas.GetLeft(window.ActionBar).ShouldBe(expected.X, 0.01);
        Canvas.GetTop(window.ActionBar).ShouldBe(expected.Y, 0.01);
    }

    [AvaloniaFact]
    public void 光标_按当前工具分流()
    {
        var window = CreateWithSelection();

        window.MouseMove(new Point(200, 200));
        ReferenceEquals(window.Cursor, CaptureWindow.MoveCursorValue).ShouldBeTrue();

        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseMove(new Point(200, 200));
        ReferenceEquals(window.Cursor, CaptureWindow.CrossCursorValue).ShouldBeTrue();

        window.TextToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseMove(new Point(200, 200));
        ReferenceEquals(window.Cursor, CaptureWindow.IbeamCursorValue).ShouldBeTrue();

        window.EraserToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.MouseMove(new Point(200, 200));
        ReferenceEquals(window.Cursor, CaptureWindow.CrossCursorValue).ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 窄窗口_低频动作收进更多菜单()
    {        // 阈值判断：工具组折叠后工具条实测 678 宽，700 以下收起低频动作
        CaptureWindow.ShouldCompact(500).ShouldBeTrue();
        CaptureWindow.ShouldCompact(660).ShouldBeTrue();
        CaptureWindow.ShouldCompact(700).ShouldBeFalse();
        CaptureWindow.ShouldCompact(1600).ShouldBeFalse();

        // 布局切换：收起四个低频动作，露出「更多」
        var window = new CaptureWindow();
        window.SetToolbarCompaction(true);

        window.MoreButton.IsVisible.ShouldBeTrue();
        window.SaveButton.IsVisible.ShouldBeFalse();
        window.PinButton.IsVisible.ShouldBeFalse();
        window.ScrollButton.IsVisible.ShouldBeFalse();

        // ✓（完成并复制）是主操作，任何宽度都保留
        window.CopyImageButton.IsVisible.ShouldBeTrue();

        window.SetToolbarCompaction(false);
        window.MoreButton.IsVisible.ShouldBeFalse();
        window.SaveButton.IsVisible.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void 尺寸框_输入宽高直接改选区()
    {
        var window = CreateWithSelection();   // 100,100 → 300,250 = 200×150
        window.SizeInPixels = false;

        window.WidthBox.Text = "320";
        window.HeightBox.Text = "180";
        window.CommitSizeEdit(0);

        window.Selection.Width.ShouldBe(320, 0.01);
        window.Selection.Height.ShouldBe(180, 0.01);
        window.Selection.Bounds.X.ShouldBe(100, 0.01);   // 左上角不动

        window.Close();
    }

    [AvaloniaFact]
    public void 尺寸框_锁定比例按编辑的一边推算另一边()
    {
        var window = CreateWithSelection();   // 200×150 → 4:3
        window.SizeInPixels = false;

        window.SizeLockButton.IsChecked = true;
        window.SizeLockButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.WidthBox.Text = "400";
        window.HeightBox.Text = "150";
        window.CommitSizeEdit(0);   // 改宽：高按 4:3 跟着变成 300

        window.Selection.Width.ShouldBe(400, 0.01);
        window.Selection.Height.ShouldBe(300, 0.01);

        window.HeightBox.Text = "150";
        window.CommitSizeEdit(1);   // 改高：宽反算回 200

        window.Selection.Width.ShouldBe(200, 0.01);
        window.Selection.Height.ShouldBe(150, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 选区工具条_比例预设把选区改成该比例并上锁()
    {
        var window = CreateWithSelection();   // 200×150
        window.SizeInPixels = false;

        window.ApplyRatioPreset(16.0 / 9.0, "16:9");

        window.SizeLockButton.IsChecked.ShouldBe(true);
        window.RatioButton.Content?.ToString().ShouldBe("16:9");
        (window.Selection.Width / window.Selection.Height).ShouldBe(16.0 / 9.0, 0.01);
        window.Selection.Bounds.X.ShouldBe(100, 0.01);   // 左上角不动

        // 回到自由：不再约束比例
        window.ApplyRatioPreset(0, "自由");
        window.RatioButton.Content?.ToString().ShouldBe("自由");

        window.Close();
    }

    [AvaloniaFact]
    public void 选区工具条_X坐标把选区整体平移_尺寸不变()
    {
        var window = CreateWithSelection();   // 100,100 200×150
        window.SizeInPixels = false;

        window.PosXBox.Text = "300";
        window.CommitSizeEdit(2);

        Canvas.GetLeft(window.Selection).ShouldBe(300, 0.01);
        Canvas.GetTop(window.Selection).ShouldBe(100, 0.01);
        window.Selection.Width.ShouldBe(200, 0.01);
        window.Selection.Height.ShouldBe(150, 0.01);

        window.PosYBox.Text = "120";
        window.CommitSizeEdit(3);

        Canvas.GetTop(window.Selection).ShouldBe(120, 0.01);
        window.Selection.Width.ShouldBe(200, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 选区工具条_圆角输入写回导出圆角()
    {
        var window = CreateWithSelection();

        window.RadiusBox.Text = "16";
        window.CommitSizeEdit(4);

        window.ExportCornerRadius.ShouldBe(16, 0.01);

        window.Close();
    }

    [AvaloniaFact]
    public void 画着标注工具时_按边框先拉选区_松手恢复工具()
    {
        var window = CreateWithSelection();   // 100,100 200×150
        window.RectToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.RectToolButton.Classes.Contains("selected").ShouldBeTrue();

        // 按在左边框上往左拖：应该拉伸选区，而不是画矩形
        window.MouseDown(new Point(100, 180), MouseButton.Left);
        window.MouseMove(new Point(60, 180));
        window.MouseUp(new Point(60, 180), MouseButton.Left);

        window.Selection.Width.ShouldBe(240, 1);
        window.Notes.Count.ShouldBe(0);                              // 没有误画
        window.RectToolButton.Classes.Contains("selected").ShouldBeTrue();   // 工具恢复

        window.Close();
    }

    [AvaloniaFact]
    public void 比例锁住时_拖右边保持比例()
    {
        var window = CreateWithSelection();   // 200×150 = 4:3
        window.SizeLockButton.IsChecked = true;
        window.SizeLockButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.MouseDown(new Point(300, 175), MouseButton.Left);
        window.MouseMove(new Point(400, 175));
        window.MouseUp(new Point(400, 175), MouseButton.Left);

        var rect = window.Selection.Bounds;
        rect.Width.ShouldBeGreaterThan(200);
        (rect.Width / rect.Height).ShouldBe(4.0 / 3.0, 0.02);

        window.Close();
    }

    [AvaloniaFact]
    public void 工具组_点折叠按钮激活入口工具()
    {
        var window = CreateWithSelection();

        window.ShapeGroupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.RectToolButton.Classes.Contains("selected").ShouldBeTrue();    // 入口 = 矩形
        window.ShapeGroupButton.Classes.Contains("selected").ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 工具组_悬停弹出选项条_点选项切换入口()
    {
        var window = CreateWithSelection();

        var origin = window.ShapeGroupButton.TranslatePoint(new Point(0, 0), window)!.Value;
        window.MouseMove(new Point(origin.X + 16, origin.Y + 16));

        window.ToolGroupPopover.IsVisible.ShouldBeTrue();
        window.ToolGroupOptions.Children.Count.ShouldBe(3);

        // 反序排：括号、入口（矩形）、椭圆
        var ellipseOption = (Button)window.ToolGroupOptions.Children[2];
        ellipseOption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        window.EllipseToolButton.Classes.Contains("selected").ShouldBeTrue();
        window.RectToolButton.Classes.Contains("selected").ShouldBeFalse();
        window.ToolGroupPopover.IsVisible.ShouldBeFalse();

        window.Close();
    }

    [AvaloniaFact]
    public void 工具组_键盘选中组内工具时折叠按钮跟着换图标()
    {
        var window = CreateWithSelection();

        // 3 = 椭圆，属于形状组
        window.KeyPress(Key.D3, RawInputModifiers.None, PhysicalKey.Digit3, null);
        window.EllipseToolButton.Classes.Contains("selected").ShouldBeTrue();
        window.ShapeGroupButton.Classes.Contains("selected").ShouldBeTrue();

        // 折叠按钮的图标跟着换成椭圆
        ReferenceEquals(window.ShapeGroupIcon.Data, window.FindResource("ToolEllipse"))
            .ShouldBeTrue();

        // 点折叠按钮 = 激活入口（椭圆），不是取消
        window.ShapeGroupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.EllipseToolButton.Classes.Contains("selected").ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 取色_从冻结帧读到像素颜色_并支持三种格式()
    {
        var window = new CaptureWindow();
        window.Width = 4;
        window.Height = 4;

        var pixels = new byte[4 * 4 * 4];
        void Set(int x, int y, byte r, byte g, byte b)
        {
            var i = (y * 4 + x) * 4;   // BGRA
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        Set(1, 1, 0xE5, 0xA4, 0x4C);
        window.SetFrozenPixelsForTest(pixels, 4, 4);

        var color = window.SampleColorAt(new Point(1, 1));
        color.ShouldNotBeNull();
        color!.Value.R.ShouldBe((byte)0xE5);
        color.Value.G.ShouldBe((byte)0xA4);
        color.Value.B.ShouldBe((byte)0x4C);

        CaptureWindow.FormatColor(color.Value, 0).ShouldBe("#E5A44C");
        CaptureWindow.FormatColor(color.Value, 1).ShouldBe("rgb(229, 164, 76)");
        CaptureWindow.FormatColor(color.Value, 2).ShouldStartWith("hsl(");

        window.Close();
    }

    [AvaloniaFact]
    public void 取色_按I进入_Esc退出()
    {
        var window = CreateWithSelection();

        window.ColorSampling.ShouldBeFalse();
        window.KeyPress(Key.I, RawInputModifiers.None, PhysicalKey.I, null);
        window.ColorSampling.ShouldBeTrue();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.ColorSampling.ShouldBeFalse();

        window.Close();
    }

    [AvaloniaFact]
    public void 右键_逐级退出工具_选区_截图()
    {
        var window = CreateWithSelection();
        window.ActionBar.IsVisible.ShouldBeTrue();

        // 1) 选中工具后右键：只退出工具，选区和工具条都还在
        window.KeyPress(Key.D2, RawInputModifiers.None, PhysicalKey.Digit2, null);
        window.MouseDown(new Point(50, 50), MouseButton.Right);
        window.ActionBar.IsVisible.ShouldBeTrue();
        window.Selection.IsVisible.ShouldBeTrue();

        // 2) 再右键：清空选区，回到框选
        window.MouseDown(new Point(50, 50), MouseButton.Right);
        window.ActionBar.IsVisible.ShouldBeFalse();
        window.Selection.IsVisible.ShouldBeFalse();
        window.HintBar.IsVisible.ShouldBeTrue();

        // 3) 再右键：取消截图
        window.MouseDown(new Point(50, 50), MouseButton.Right);
        window.IsVisible.ShouldBeFalse();
        window.Action.ShouldBe(CaptureAction.None);
    }

    [AvaloniaFact]
    public void 尺寸框_超出窗口时钳制()
    {
        var window = CreateWithSelection();
        window.SizeInPixels = false;

        window.WidthBox.Text = "99999";
        window.HeightBox.Text = "99999";
        window.CommitSizeEdit(0);

        (window.Selection.Bounds.X + window.Selection.Width)
            .ShouldBeLessThanOrEqualTo(window.Width + 0.01);
        (window.Selection.Bounds.Y + window.Selection.Height)
            .ShouldBeLessThanOrEqualTo(window.Height + 0.01);

        window.Close();
    }

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

    /// <summary>选区之外压暗、选区内保持原图亮度：遮罩用 Exclude 挖洞。</summary>
    [AvaloniaFact]
    public void 选区遮罩_按选区挖洞只压暗外面()
    {
        var window = new CaptureWindow();
        window.Show();

        window.MouseDown(new Point(100, 100), MouseButton.Left);
        window.MouseMove(new Point(300, 250));
        window.MouseUp(new Point(300, 250), MouseButton.Left);

        var combined = window.DimLayer.Data.ShouldBeOfType<CombinedGeometry>();
        combined.GeometryCombineMode.ShouldBe(GeometryCombineMode.Exclude);

        var hole = combined.Geometry2.ShouldBeOfType<RectangleGeometry>();
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
