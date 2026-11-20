using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
        window.SizeText.Text.ShouldNotBeNullOrEmpty();
        window.SizeText.Text.ShouldContain("×");

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
    public void 工具条_下方放不下时翻到选区上方()
    {
        var selection = new Rect(100, 900, 300, 100);
        var position = CaptureWindow.ComputeToolbarPosition(
            selection, new Size(400, 40), new Size(1280, 1000));

        position.Y.ShouldBe(900 - 40 - 8);
        position.X.ShouldBe(8);   // 右对齐后贴到左边距（选区右边缘只有 400）
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
}
