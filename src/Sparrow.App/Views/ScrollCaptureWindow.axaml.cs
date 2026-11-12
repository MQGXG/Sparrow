using Avalonia.Controls;
using Avalonia.Interactivity;

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
