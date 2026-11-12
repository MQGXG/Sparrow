using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sparrow.App.Services;

namespace Sparrow.App.Views;

/// <summary>
/// 全屏选区窗口。
/// 用鼠标拖出矩形，松开后截取该区域（物理像素），关窗并返回 PNG。
/// </summary>
public partial class CaptureWindow : Window
{
    private Point? _start;
    private PixelPoint _origin;
    private double _scaling = 1;

    public CaptureWindow() => InitializeComponent();

    /// <summary>选区的 PNG 结果；取消或选区过小时为 null。</summary>
    public byte[]? Result { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyVirtualScreenBounds();
    }

    /// <summary>
    /// 覆盖所有显示器的并集，而不是只覆盖主屏。
    /// 多屏缩放不同时，以主屏缩放为基准（M1 已知限制）。
    /// </summary>
    private void ApplyVirtualScreenBounds()
    {
        var screens = Screens.All;
        if (screens.Count == 0)
            return;

        var left = screens.Min(screen => screen.Bounds.X);
        var top = screens.Min(screen => screen.Bounds.Y);
        var right = screens.Max(screen => screen.Bounds.X + screen.Bounds.Width);
        var bottom = screens.Max(screen => screen.Bounds.Y + screen.Bounds.Height);

        _origin = new PixelPoint(left, top);
        _scaling = Screens.Primary?.Scaling ?? RenderScaling;

        Position = _origin;
        Width = (right - left) / _scaling;
        Height = (bottom - top) / _scaling;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _start = e.GetPosition(this);

        Selection.IsVisible = true;
        Canvas.SetLeft(Selection, _start.Value.X);
        Canvas.SetTop(Selection, _start.Value.Y);
        Selection.Width = 0;
        Selection.Height = 0;

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_start is null)
            return;

        UpdateSelection(e.GetPosition(this));
        e.Handled = true;
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_start is null)
            return;

        var end = e.GetPosition(this);
        UpdateSelection(end);

        var start = _start.Value;
        _start = null;

        var rect = new Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));

        if (rect.Width < 4 || rect.Height < 4)
        {
            Close();
            return;
        }

        var scaling = _scaling;
        var origin = _origin;

        var x = origin.X + (int)(rect.X * scaling);
        var y = origin.Y + (int)(rect.Y * scaling);
        var width = (int)(rect.Width * scaling);
        var height = (int)(rect.Height * scaling);

        // 先隐藏遮罩，再截图，否则截到的是自己
        Hide();
        await Task.Delay(80);

        Result = ScreenCapture.Capture(x, y, width, height);
        Close();
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

    private void UpdateSelection(Point current)
    {
        if (_start is null)
            return;

        var start = _start.Value;
        var x = Math.Min(start.X, current.X);
        var y = Math.Min(start.Y, current.Y);

        Canvas.SetLeft(Selection, x);
        Canvas.SetTop(Selection, y);
        Selection.Width = Math.Abs(current.X - start.X);
        Selection.Height = Math.Abs(current.Y - start.Y);
    }
}
