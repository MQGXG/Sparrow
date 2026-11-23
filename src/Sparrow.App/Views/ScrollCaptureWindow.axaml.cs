using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Sparrow.App.Views;

/// <summary>
/// 滚动截图时的悬浮提示条。
///
/// 关键点：ShowActivated=false、不抢焦点。用户还要继续用滚轮操作下面的页面，
/// 如果这个窗口抢走焦点，滚轮事件就会送到覆盖层。
/// 停止捕获靠「完成」按钮，而不是 Enter——因为窗口本来就没有焦点。
/// </summary>
public partial class ScrollCaptureWindow : Window
{
    private WriteableBitmap? _preview;

    public ScrollCaptureWindow() => InitializeComponent();

    /// <summary>用户点了「完成」，保留已捕获内容。</summary>
    public event EventHandler? Finished;

    /// <summary>用户点了「取消」，丢弃结果。</summary>
    public event EventHandler? Cancelled;

    /// <summary>更新已捕获的屏数。</summary>
    public void SetCaptured(int rows, int frameHeight)
    {
        if (frameHeight <= 0)
            return;

        var screens = rows / (double)frameHeight;
        ProgressText.Text = $"已捕获 {screens:F1} 屏（{rows} px）";
    }

    /// <summary>更新拼接结果的缩略图；解码失败就保持上一张。</summary>
    public void SetPreview(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            var bitmap = WriteableBitmap.Decode(stream);

            if (bitmap is null)
                return;

            _preview?.Dispose();
            _preview = bitmap;
            PreviewImage.Source = bitmap;
            PreviewImage.IsVisible = true;
        }
        catch
        {
            // 预览失败不影响滚动截图本身
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _preview?.Dispose();
        _preview = null;
        base.OnClosed(e);
    }

    private void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        Finished?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }
}
