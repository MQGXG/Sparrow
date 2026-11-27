// CaptureWindowTests 分部类：选区拖拽、边界约束与放大镜。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Shouldly;
using SkiaSharp;
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

        // 原选区右边 = 100 + 200，拉伸左边时它必须钉住
        var right = Canvas.GetLeft(window.Selection) + window.Selection.Width;

        window.MouseDown(new Point(100, 175), MouseButton.Left);
        window.MouseMove(new Point(50, 175));
        window.MouseUp(new Point(50, 175), MouseButton.Left);

        // 【修改】左边可能被吸附参考线拉近（真实桌面的窗口边/屏幕中线，
        // 最多 SnapGuides.Tolerance），因此左边和宽度按吸附阈值断言；
        // 这条测试真正要守住的是“右边不动”。
        Canvas.GetLeft(window.Selection).ShouldBe(50, SnapGuides.Tolerance);
        window.Selection.Width.ShouldBe(250, SnapGuides.Tolerance);
        (Canvas.GetLeft(window.Selection) + window.Selection.Width)
            .ShouldBe(right, 0.01);

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

    // 1×1 透明 PNG：纯标注模式只需要能解码成 Bitmap
    private static readonly byte[] AnnotationModePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// 贴图点「标注」后，图片就是选区。整体拖动时底图必须跟着一起走，
    /// 且选区框 / 手柄 / 尺寸角标不能重新冒出来（旧实现里拖动后二者会分离，
    /// 标注还会被反向平移导致导出错位）。
    /// </summary>
    [AvaloniaFact]
    public void 标注模式_拖动后底图与选区同步且不显示选区框()
    {
        var window = new CaptureWindow
        {
            AnnotationOnly = true,
            AnnotationSource = AnnotationModePng,
            FreezeOnOpen = false
        };
        window.Show();

        var left = Canvas.GetLeft(window.FrozenFrame);
        var top = Canvas.GetTop(window.FrozenFrame);
        var imageWidth = window.FrozenFrame.Width;
        var imageHeight = window.FrozenFrame.Height;

        window.MouseDown(new Point(left + 20, top + 20), MouseButton.Left);
        window.MouseMove(new Point(left + 60, top + 45));
        window.MouseUp(new Point(left + 60, top + 45), MouseButton.Left);

        // 底图整体平移，尺寸不变
        Canvas.GetLeft(window.FrozenFrame).ShouldBe(left + 40, 0.01);
        Canvas.GetTop(window.FrozenFrame).ShouldBe(top + 25, 0.01);
        window.FrozenFrame.Width.ShouldBe(imageWidth, 0.01);
        window.FrozenFrame.Height.ShouldBe(imageHeight, 0.01);

        // 选区 UI 永远不出现在标注模式
        window.Selection.IsVisible.ShouldBeFalse();
        window.HandleLayer.IsVisible.ShouldBeFalse();
        window.SizeBadge.IsVisible.ShouldBeFalse();

        window.Close();
    }

    /// <summary>
    /// 贴图标注层必须有压暗背景：标注层如果全透明，桌面和后面的贴图窗口会一起露出来，
    /// 看起来就像同一张图被复制了一份。工具条也要贴在图片下方、跟随图片移动。
    /// </summary>
    [AvaloniaFact]
    public void 标注模式_背景压暗且工具条跟随图片()
    {
        var window = new CaptureWindow
        {
            AnnotationOnly = true,
            AnnotationSource = AnnotationModePng,
            FreezeOnOpen = false
        };
        window.Show();

        window.DimLayer.IsVisible.ShouldBeTrue();

        var imageLeft = Canvas.GetLeft(window.FrozenFrame);
        var imageTop = Canvas.GetTop(window.FrozenFrame);
        var barTop = Canvas.GetTop(window.ActionBar);

        window.MouseDown(new Point(imageLeft + 20, imageTop + 20), MouseButton.Left);
        window.MouseMove(new Point(imageLeft + 60, imageTop + 45));
        window.MouseUp(new Point(imageLeft + 60, imageTop + 45), MouseButton.Left);

        // 图片整体下移 25，工具条跟着下移同样的距离
        Canvas.GetTop(window.ActionBar).ShouldBe(barTop + 25, 0.01);

        window.Close();
    }

    /// <summary>
    /// 贴图标注：图片停在宿主给的位置、不遮罩屏幕；其余区域交给桌面，不影响正常操作。
    /// </summary>
    [AvaloniaFact]
    public void 标注模式_指定图片区域时图片停在原位且不遮罩()
    {
        var window = new CaptureWindow
        {
            AnnotationOnly = true,
            AnnotationSource = CreatePng(320, 200),
            FreezeOnOpen = false,
            AnnotationBounds = new PixelRect(200, 120, 320, 200)
        };

        window.Show();

        window.DimLayer.IsVisible.ShouldBeFalse();

        var scale = window.RenderScaling > 0 ? window.RenderScaling : 1;

        // 图片停在宿主给的位置（窗口左上角是虚拟屏幕原点）
        Canvas.GetLeft(window.FrozenFrame).ShouldBe((200 - window.Position.X) / scale, 0.01);
        Canvas.GetTop(window.FrozenFrame).ShouldBe((120 - window.Position.Y) / scale, 0.01);
        window.FrozenFrame.Width.ShouldBe(320 / scale, 0.01);
        window.FrozenFrame.Height.ShouldBe(200 / scale, 0.01);

        // 工具条贴在图片下方
        Canvas.GetTop(window.ActionBar).ShouldBeGreaterThan(
            Canvas.GetTop(window.FrozenFrame) + window.FrozenFrame.Height);

        window.Close();
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// 滚动长图：标注时按原始比例完整显示。旧实现给宽/高分别设最小 160/120，
    /// 细长图会被横向拉宽，标注合成回原图时就会错位。
    /// </summary>
    [AvaloniaFact]
    public void 标注模式_长图保持原始比例()
    {
        const int width = 120;
        const int height = 4000;

        var window = new CaptureWindow
        {
            AnnotationOnly = true,
            AnnotationSource = CreatePng(width, height),
            FreezeOnOpen = false
        };
        window.Show();

        var shownWidth = window.FrozenFrame.Width;
        var shownHeight = window.FrozenFrame.Height;

        (shownWidth / shownHeight).ShouldBe((double)width / height, 0.001);

        // 不能超出窗口高度，否则工具条也没地方放
        shownHeight.ShouldBeLessThanOrEqualTo(window.Height);

        window.Close();
    }
}
