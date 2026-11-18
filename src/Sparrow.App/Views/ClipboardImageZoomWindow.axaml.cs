using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Sparrow.App.Views;

/// <summary>
/// 剪贴板详情的图片灯箱。
///
/// 详情窗口只有 430 宽，图片再怎么 Uniform 也放大不了；这里铺满详情所在的那块屏幕，
/// 用 Uniform 完整显示，点击任意处或 Esc 关闭。
/// </summary>
public partial class ClipboardImageZoomWindow : Window
{
    public ClipboardImageZoomWindow()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    public ClipboardImageZoomWindow(byte[] png, Window owner) : this()
    {
        using var stream = new MemoryStream(png);
        Preview.Source = new Bitmap(stream);

        PlaceOnOwnerScreen(owner);
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
