using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sparrow.App.Services;

namespace Sparrow.App.Overlays;

/// <summary>
/// 录制前的全屏倒计时：半透明遮罩 + 大数字，Esc 取消。
/// 结果 Task 为 true 继续录制，false 表示用户取消。
/// </summary>
internal sealed class CountdownWindow : Window
{
    private readonly int _seconds;
    private readonly TextBlock _number;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _result = new();

    public Task<bool> Result => _result.Task;

    public CountdownWindow(int seconds)
    {
        _seconds = Math.Max(1, seconds);

        WindowDecorations = WindowDecorations.None;
        WindowState = WindowState.FullScreen;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));

        _number = new TextBlock
        {
            Text = _seconds.ToString(),
            FontSize = 140,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        Content = new Grid
        {
            Children =
            {
                _number,
                new TextBlock
                {
                    Text = "即将开始录制 · Esc 取消",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(0, 200, 0, 0)
                }
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            _cts.Cancel();
            Finish(false);
        };

        Opened += (_, _) =>
        {
            // 倒计时也不能出现在录制的视频里。
            WindowCaptureExclusion.Exclude(this);
            _ = RunAsync();
        };
    }

    private async Task RunAsync()
    {
        for (var remaining = _seconds; remaining > 0; remaining--)
        {
            if (_cts.IsCancellationRequested)
            {
                Finish(false);
                return;
            }

            _number.Text = remaining.ToString();

            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Finish(false);
                return;
            }
        }

        Finish(true);
    }

    private void Finish(bool proceed)
    {
        if (_result.Task.IsCompleted)
            return;

        _result.TrySetResult(proceed);
        Close();
    }
}
