using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Sparrow.App.Views;

/// <summary>
/// 贴图：把截图钉在屏幕上，置顶、可拖动、Esc 关闭。
/// 可以同时打开多个，由 App 负责持有引用。
/// </summary>
public partial class PinnedImageWindow : Window
{
    private const double MinZoom = 0.5;
    private const double MaxZoom = 2.0;
    private const double ZoomStep = 0.1;

    private Bitmap? _bitmap;
    private double _baseWidth;
    private double _baseHeight;
    private double _zoom = 1.0;

    /// <summary>设计器/运行时加载器需要的无参构造。</summary>
    public PinnedImageWindow()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerEntered += (_, _) => CloseButton.Opacity = 1;
        PointerMoved += (_, _) => CloseButton.Opacity = 1;
        PointerExited += (_, _) => CloseButton.Opacity = 0.35;
        DoubleTapped += (_, _) => Close();
    }

    public PinnedImageWindow(byte[] png) : this()
    {
        using var stream = new MemoryStream(png);
        _bitmap = new Bitmap(stream);
        Preview.Source = _bitmap;

        // 按图片比例定窗口大小，最多占工作区宽度 80%
        var scaling = Screens.Primary?.Scaling ?? 1;
        var workWidth = Screens.Primary?.WorkingArea.Width ?? 1920;
        var maxWidth = workWidth * 0.8;

        var scale = Math.Min(1.0, maxWidth / _bitmap.PixelSize.Width);
        Width = Math.Max(80, _bitmap.PixelSize.Width * scale / scaling);
        Height = Math.Max(60, _bitmap.PixelSize.Height * scale / scaling);

        // 记住 100% 时的尺寸，滚轮在这个基准上缩放
        _baseWidth = Width;
        _baseHeight = Height;

        // 首次打开短暂露出关闭按钮，之后只在悬停时出现
        _ = ShowCloseHintAsync();
    }

    private async Task ShowCloseHintAsync()
    {
        CloseButton.Opacity = 1;
        await Task.Delay(1500);

        if (!IsPointerOver)
            CloseButton.Opacity = 0.35;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点关闭按钮时不要触发拖动
        if (e.Source is Visual source
            && (source is Button || source.FindAncestorOfType<Button>() is not null))
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>滚轮缩放：以窗口中心为锚点，范围 0.5–2.0。</summary>
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || _baseWidth <= 0 || _baseHeight <= 0)
            return;

        ApplyZoom(_zoom + (e.Delta.Y > 0 ? ZoomStep : -ZoomStep));
        e.Handled = true;
    }

    private void ApplyZoom(double zoom)
    {
        var next = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 0.001)
            return;

        // Position 是物理像素，Width/Height 是 DIP，换算后才能保持窗口中心不动
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        var centerX = Position.X + Width * scaling / 2;
        var centerY = Position.Y + Height * scaling / 2;

        _zoom = next;
        Width = _baseWidth * _zoom;
        Height = _baseHeight * _zoom;

        Position = new PixelPoint(
            (int)Math.Round(centerX - Width * scaling / 2),
            (int)Math.Round(centerY - Height * scaling / 2));
    }
}
