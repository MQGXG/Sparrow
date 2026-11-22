using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sparrow.App.Services;

namespace Sparrow.App.Views;

/// <summary>
/// 滚动截图的结果预览。
///
/// 复用普通截图的动作语义（<see cref="CaptureAction"/>），
/// 由 App 统一执行，这样「识别 / 复制 / 保存 / 固定」只有一套实现。
/// </summary>
public partial class ScrollResultWindow : Window
{
    private Bitmap? _bitmap;

    /// <summary>设计器需要的无参构造。</summary>
    public ScrollResultWindow() => InitializeComponent();

    public ScrollResultWindow(byte[] png) : this()
    {
        var (width, height) = ImageSizeReader.Read(png);

        using var stream = new MemoryStream(png);
        var targetWidth = PreviewWidth(width, height);

        // 长图预览不按原尺寸解码：几万行的位图会直接吃掉几百 MB。
        // 导出的 PNG 仍是原尺寸，预览只是按总像素预算缩放。
        _bitmap = targetWidth > 0 && targetWidth < width
            ? Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.HighQuality)
            : new Bitmap(stream);

        Preview.Source = _bitmap;

        width = width > 0 ? width : _bitmap.PixelSize.Width;
        height = height > 0 ? height : _bitmap.PixelSize.Height;
        SizeText.Text = $"{width} × {height} px  ·  Esc 关闭";
    }

    /// <summary>预览最多约 16 MP；长图按比例缩到预算内，避免整张原图进内存。</summary>
    private static int PreviewWidth(int width, int height)
    {
        const long budget = 16L * 1024 * 1024;

        if (width <= 0 || height <= 0)
            return 0;

        var pixels = (long)width * height;
        if (pixels <= budget)
            return width;

        return Math.Max(1, (int)Math.Round(width * Math.Sqrt((double)budget / pixels)));
    }

    /// <summary>用户选择的动作；关闭时为 <see cref="CaptureAction.None"/>。</summary>
    public CaptureAction Action { get; private set; }

    protected override void OnClosed(EventArgs e)
    {
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnClosed(e);
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnRecognizeClick(object? sender, RoutedEventArgs e) => CloseWith(CaptureAction.Recognize);

    private void OnCopyImageClick(object? sender, RoutedEventArgs e) => CloseWith(CaptureAction.CopyImage);

    private void OnSaveClick(object? sender, RoutedEventArgs e) => CloseWith(CaptureAction.Save);

    private void OnPinClick(object? sender, RoutedEventArgs e) => CloseWith(CaptureAction.Pin);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void CloseWith(CaptureAction action)
    {
        Action = action;
        Close();
    }
}
