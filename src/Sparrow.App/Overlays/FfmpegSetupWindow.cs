using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;

namespace Sparrow.App.Overlays;

/// <summary>
/// 首次录制时缺少 FFmpeg：显示下载进度，完成后返回 ffmpeg.exe 路径。
/// 用户取消或下载失败时 <see cref="Result"/> 返回 null，<see cref="Error"/> 给出原因。
/// </summary>
internal sealed class FfmpegSetupWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string?> _result = new();

    public Task<string?> Result => _result.Task;

    /// <summary>下载/解压失败的原因（取消时为 null）。</summary>
    public string? Error { get; private set; }

    public FfmpegSetupWindow()
    {
        Width = 440;
        Height = 208;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        _status = new TextBlock
        {
            Text = "首次录制需要下载 FFmpeg 编码器（约 120 MB，仅下载一次）…",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#CCDDDDDD")),
            TextWrapping = TextWrapping.Wrap
        };

        _bar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Value = 0
        };

        var cancel = new Button
        {
            Content = "取消",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 5),
            Background = new SolidColorBrush(Color.Parse("#33FFFFFF")),
            Foreground = Brushes.White
        };

        cancel.Click += (_, _) =>
        {
            _cts.Cancel();
            Close();
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21A1A1A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#44FFFFFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 16),
            BoxShadow = BoxShadows.Parse("0 12 32 0 #99000000"),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "准备录制编码器",
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    },
                    _status,
                    _bar,
                    cancel
                }
            }
        };

        Opened += (_, _) =>
        {
            // 下载窗口不是内容，不进任何捕获。
            WindowCaptureExclusion.Exclude(this);
            _ = RunAsync();
        };
    }

    private async Task RunAsync()
    {
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (value == -2)
                {
                    _bar.IsIndeterminate = true;
                    _status.Text = "正在解压 FFmpeg 编码器…";
                    return;
                }

                if (value < 0)
                {
                    _bar.IsIndeterminate = true;
                    _status.Text = "正在下载 FFmpeg 编码器…";
                    return;
                }

                _bar.IsIndeterminate = false;
                _bar.Value = Math.Clamp(value * 100, 0, 100);
                _status.Text = $"正在下载 FFmpeg 编码器… {value:P0}";
            });

            var path = await FfmpegLocator.EnsureAsync(progress, _cts.Token);

            if (path is null)
                Error = "未找到 ffmpeg.exe。";

            _result.TrySetResult(path);
        }
        catch (OperationCanceledException)
        {
            _result.TrySetResult(null);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _result.TrySetResult(null);
        }
        finally
        {
            Close();
        }
    }
}
