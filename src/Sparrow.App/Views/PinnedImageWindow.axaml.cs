using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Sparrow.App.Views;

/// <summary>
/// 贴图：把截图钉在屏幕上，置顶、可拖动、Esc 关闭。
/// 可以同时打开多个，由 App 负责持有引用。
/// </summary>
public partial class PinnedImageWindow : Window
{
    private Bitmap? _bitmap;

    /// <summary>设计器/运行时加载器需要的无参构造。</summary>
    public PinnedImageWindow()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
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
    }

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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
