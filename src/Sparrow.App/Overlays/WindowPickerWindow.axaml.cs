using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Sparrow.App.Overlays;

/// <summary>
/// 全屏拾取窗口：图钉光标跟随鼠标，左键选择窗口，右键 / Esc 取消。
///
/// 点击时先隐藏自己再解析光标下的窗口——遮罩本身不能成为命中目标；
/// 若光标下仍是本进程的窗口（命令面板 / 工作区），
/// <see cref="WindowPickerApi.ResolveAtCursor"/> 会继续往下穿透。
/// </summary>
public partial class WindowPickerWindow : Window
{
    private readonly TaskCompletionSource<nint> _picked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenRegistration _registration;

    /// <summary>XAML 资源加载器需要无参构造；实际调用请用带提示文案的构造。</summary>
    public WindowPickerWindow() : this(string.Empty)
    {
    }

    public WindowPickerWindow(string hint)
    {
        InitializeComponent();

        HintText.Text = hint;

        AddHandler(PointerMovedEvent, OnPointerMoved);
        AddHandler(PointerPressedEvent, OnPointerPressed);
        AddHandler(KeyDownEvent, OnKeyDown);
    }

    /// <summary>显示遮罩并等待选择；取消返回 0。</summary>
    public Task<nint> PickAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _registration = cancellationToken.Register(
                () => Dispatcher.UIThread.Post(() => Finish(0)));
        }

        Show();
        Activate();
        Focus();

        return _picked.Task;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        CoverAllScreens();
    }

    /// <summary>覆盖整个虚拟桌面（多显示器取并集）。</summary>
    private void CoverAllScreens()
    {
        var screens = Screens.All;
        if (screens.Count == 0)
            return;

        var left = screens.Min(screen => screen.Bounds.X);
        var top = screens.Min(screen => screen.Bounds.Y);
        var right = screens.Max(screen => screen.Bounds.Right);
        var bottom = screens.Max(screen => screen.Bounds.Bottom);

        // 位置是物理像素，宽高是 DIP：按主屏缩放换算
        var scale = Screens.Primary?.Scaling ?? 1.0;
        Position = new PixelPoint(left, top);
        Width = (right - left) / scale;
        Height = (bottom - top) / scale;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(this);

        PinGlyph.IsVisible = true;
        Canvas.SetLeft(PinGlyph, position.X - 12);
        Canvas.SetTop(PinGlyph, position.Y - 30);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsRightButtonPressed)
        {
            Finish(0);
            return;
        }

        if (!properties.IsLeftButtonPressed)
            return;

        // 先隐藏自己，再解析：否则命中的一定是遮罩
        Hide();
        Finish(WindowPickerApi.ResolveAtCursor());
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        Finish(0);
    }

    private void Finish(nint handle)
    {
        if (_picked.Task.IsCompleted)
            return;

        _picked.TrySetResult(handle);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _registration.Dispose();
        _picked.TrySetResult(0);
        base.OnClosed(e);
    }
}
