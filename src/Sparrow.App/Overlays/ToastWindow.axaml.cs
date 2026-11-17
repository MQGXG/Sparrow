using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Sparrow.App.Overlays;

/// <summary>
/// 应用内轻提示：右上角、不抢焦点、定时自动消失、点击立即关闭。
///
/// 为什么不用 Windows 原生通知：非打包 Win32 应用要配 AUMID + 快捷方式，
/// 而且消息会留在通知中心；这里的提示都是「当前操作的结果」，看一眼就该消失。
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _timer;

    /// <summary>XAML 资源加载器需要无参构造；实际调用请用带消息的构造。</summary>
    public ToastWindow() : this(string.Empty)
    {
    }

    public ToastWindow(string message, TimeSpan? duration = null)
    {
        InitializeComponent();

        MessageText.Text = message;

        _timer = new DispatcherTimer { Interval = duration ?? DefaultDuration };
        _timer.Tick += (_, _) => Close();

        PointerPressed += OnPointerPressed;
        Opened += OnOpened;
        Closed += (_, _) => _timer.Stop();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        PositionTopRight();
        _timer.Start();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    /// <summary>贴主屏工作区右上角；位置是物理像素，宽度是 DIP。</summary>
    private void PositionTopRight()
    {
        var screens = Screens;
        if (screens.Primary is not { } primary)
            return;

        var area = primary.WorkingArea;
        var scale = primary.Scaling <= 0 ? 1.0 : primary.Scaling;
        var width = (int)(Width * scale);

        Position = new PixelPoint(area.Right - width - 16, area.Y + 16);
    }
}
