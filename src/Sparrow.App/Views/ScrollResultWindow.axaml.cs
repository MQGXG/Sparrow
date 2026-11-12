using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

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
        using var stream = new MemoryStream(png);
        _bitmap = new Bitmap(stream);

        Preview.Source = _bitmap;
        SizeText.Text = $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height} px  ·  Esc 关闭";
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
