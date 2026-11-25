// CaptureWindowTests 分部类：工具条、折叠、尺寸框、调色板、取色与右键。
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
    [AvaloniaFact]
    public void 工具按钮可键盘聚焦_方向键打开工具组()
    {
        var window = CreateWithSelection();
        window.ToolGroupShowDelayMs = 0;

        window.ShapeGroupButton.Focusable.ShouldBeTrue();
        window.ShapeGroupButton.Focus();
        window.ShapeGroupButton.IsFocused.ShouldBeTrue();

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);

        window.ToolGroupPopover.IsVisible.ShouldBeTrue();
        window.ToolGroupOptions.Children[0].IsFocused.ShouldBeTrue();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        window.ToolGroupPopover.IsVisible.ShouldBeFalse();
        window.ShapeGroupButton.IsFocused.ShouldBeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public void 工具组悬停有显示延迟()
    {
        var window = CreateWithSelection();
        window.ToolGroupShowDelayMs = 500;

        var origin = window.ShapeGroupButton.TranslatePoint(new Point(0, 0), window)!.Value;
        window.MouseMove(new Point(origin.X + 16, origin.Y + 16));

        window.ToolGroupPopover.IsVisible.ShouldBeFalse();

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
        window.ToolGroupShowDelayMs = 0;

        var origin = window.ShapeGroupButton.TranslatePoint(new Point(0, 0), window)!.Value;
        window.MouseMove(new Point(origin.X + 16, origin.Y + 16));

        window.ToolGroupPopover.IsVisible.ShouldBeTrue();
        window.ToolGroupOptions.Children.Count.ShouldBe(4);

        // 反序排：入口（矩形）、括号、椭圆、菱形
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
}
