using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sparrow.App.Services;

namespace Sparrow.App.Overlays;

/// <summary>
/// 录制前的摄像头调整提示条：预览保持显示，用户拖动/缩放好后点“开始录制”。
/// 提示条本身排除捕获，不会进视频。
/// </summary>
internal sealed class CameraSetupPromptWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();

    public Task<bool> Result => _result.Task;

    public CameraSetupPromptWindow(PixelRect region)
    {
        Width = 380;
        Height = 96;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        PixelRect screen;

        try
        {
            screen = Screens.Primary?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        }
        catch
        {
            screen = new PixelRect(0, 0, 1920, 1080);
        }

        var bounds = region.Width > 0 && region.Height > 0 ? region : screen;

        Position = new PixelPoint(
            Math.Clamp(bounds.X + (bounds.Width - 380) / 2, screen.X + 8, Math.Max(screen.X + 8, screen.Right - 388)),
            Math.Clamp(bounds.Y + 16, screen.Y + 8, Math.Max(screen.Y + 8, screen.Bottom - 104)));

        var start = new Button
        {
            Content = "开始录制",
            Padding = new Thickness(16, 6),
            Background = new SolidColorBrush(Color.Parse("#E5A44C")),
            Foreground = Brushes.Black,
            FontWeight = FontWeight.SemiBold
        };

        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(12, 6),
            Background = new SolidColorBrush(Color.Parse("#33FFFFFF")),
            Foreground = Brushes.White
        };

        start.Click += (_, _) => Finish(true);
        cancel.Click += (_, _) => Finish(false);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21A1A1A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#44FFFFFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12),
            BoxShadow = BoxShadows.Parse("0 12 32 0 #99000000"),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "调整摄像头画中画",
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = "拖动预览移动位置，拖右下角缩放；调整好后开始录制。",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#CCDDDDDD")),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, start }
                    }
                }
            }
        };

        Opened += (_, _) =>
        {
            WindowCaptureExclusion.Exclude(this);
            Activate();
        };

        Closed += (_, _) => _result.TrySetResult(false);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Finish(true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(false);
            }
        };
    }

    private void Finish(bool start)
    {
        _result.TrySetResult(start);
        Close();
    }
}
