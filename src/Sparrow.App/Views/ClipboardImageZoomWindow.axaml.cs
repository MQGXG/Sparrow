using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sparrow.App.Services;

namespace Sparrow.App.Views;

/// <summary>
/// 剪贴板详情的图片灯箱。
///
/// 详情窗口只有 430 宽，图片再怎么 Uniform 也放大不了；这里铺满详情所在的那块屏幕，
/// 用 Uniform 完整显示，点击任意处或 Esc 关闭。
/// </summary>
public partial class ClipboardImageZoomWindow : Window
{
    /// <summary>灯箱要看原始细节，所以走原尺寸解码；关闭时必须自己释放——
    /// native 内存不归 GC 管，这是当初工作集涨上去的漏点之一。</summary>
    private Bitmap? _bitmap;

    public ClipboardImageZoomWindow()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    public ClipboardImageZoomWindow(byte[] png, Window owner) : this()
    {
        _bitmap = ImageCache.DecodeOriginal(png);
        Preview.Source = _bitmap;

        PlaceOnOwnerScreen(owner);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // 先断开引用再释放，避免窗口关闭瞬间还在渲染一张已释放的位图
        Preview.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    /// <summary>铺满详情窗口所在屏幕的工作区（逻辑坐标，按缩放换算）。</summary>
    private void PlaceOnOwnerScreen(Window owner)
    {
        var center = owner.PointToScreen(
            new Point(owner.Bounds.Width / 2, owner.Bounds.Height / 2));
        var screen = owner.Screens.ScreenFromPoint(center) ?? owner.Screens.Primary;
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;

        Position = new PixelPoint(area.X, area.Y);
        Width = area.Width / scaling;
        Height = area.Height / scaling;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Close();
        e.Handled = true;
    }

    private void OnCloseTapped(object? sender, TappedEventArgs e) => Close();
}
