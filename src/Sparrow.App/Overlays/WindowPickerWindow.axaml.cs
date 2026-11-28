using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sparrow.App.Services;

namespace Sparrow.App.Overlays;

/// <summary>
/// 全屏拾取窗口：图钉光标跟随鼠标，左键选择窗口，右键 / Esc 取消。
///
/// 点击时先隐藏自己再解析；<see cref="WindowPickerApi.ResolveAtCursor"/> 按 Z 序
/// 枚举顶层窗口，只取「覆盖光标点、非本进程、可见且非 Shell」的第一个，
/// 不再靠 Z 序盲穿透，避免置顶到不在光标下的窗口。
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

        // 窗口拾取遮罩不能出现在录制的视频里。
        WindowCaptureExclusion.Exclude(this);

        CoverAllScreens();

        // 进入拾取就立刻在光标处显示图钉：否则系统光标已隐藏、用户不动鼠标会以为没反应
        if (WindowPickerApi.TryGetCursor(out var cursor))
        {
            var local = new Point(
                (cursor.X - Position.X) / RenderScaling,
                (cursor.Y - Position.Y) / RenderScaling);
            PlacePin(local);
        }
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
        PlacePin(e.GetPosition(this));
    }

    private void PlacePin(Point position)
    {
        PinGlyph.IsVisible = true;
        // 32px 徽章：底部贴近光标，让针尖视觉上指向鼠标位置
        Canvas.SetLeft(PinGlyph, position.X - 16);
        Canvas.SetTop(PinGlyph, position.Y - 32);
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
