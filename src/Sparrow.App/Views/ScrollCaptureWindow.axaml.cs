using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sparrow.App.Services;

namespace Sparrow.App.Views;

/// <summary>
/// 滚动截图时的悬浮状态条。
///
/// 只显示进度和完成/取消——自动/方向/速度已经在工具条的二级菜单里定好，
/// 这里再放设置会让「点开始之后还要再选一次」，也就是之前一闪而过的问题。
///
/// ShowActivated=false、不抢焦点：手动模式还要继续操作下面的页面。
/// </summary>
public partial class ScrollCaptureWindow : Window
{
    private readonly int _frameHeight;

    private WriteableBitmap? _preview;

    public ScrollCaptureWindow()
        : this(new ScrollCaptureControl(), 800)
    {
    }

    internal ScrollCaptureWindow(ScrollCaptureControl control, int frameHeight)
    {
        InitializeComponent();

        _frameHeight = Math.Max(1, frameHeight);

        HintText.Text = control.AutoScroll
            ? "正在自动滚动…"
            : "请用滚轮滚动页面，完成后点「完成」";

        Opened += (_, _) => WindowCaptureExclusion.Exclude(this);
    }

    /// <summary>用户点了「完成」，保留已捕获内容。</summary>
    public event EventHandler? Finished;

    /// <summary>用户点了「取消」，丢弃结果。</summary>
    public event EventHandler? Cancelled;

    /// <summary>更新已捕获的屏数。</summary>
    public void SetCaptured(int rows, int frameHeight)
    {
        var height = frameHeight > 0 ? frameHeight : _frameHeight;
        var screens = rows / (double)height;
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
