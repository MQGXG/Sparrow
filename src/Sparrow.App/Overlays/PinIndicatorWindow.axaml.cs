using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Sparrow.App.Overlays;

/// <summary>
/// 被置顶窗口标题栏旁的图钉：50ms 跟随一次，hover 变 ✕，点击触发取消置顶。
/// 目标窗口销毁 / 最小化时自动隐藏或关闭，不留孤儿窗口。
/// </summary>
public partial class PinIndicatorWindow : Window
{
    private readonly nint _target;
    private readonly Action _onRemove;
    private readonly DispatcherTimer _timer;

    /// <summary>XAML 资源加载器需要无参构造；实际调用请用带目标句柄与回调的构造。</summary>
    public PinIndicatorWindow() : this(0, () => { })
    {
    }

    public PinIndicatorWindow(nint target, Action onRemove)
    {
        InitializeComponent();

        _target = target;
        _onRemove = onRemove;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Follow();

        PointerEntered += (_, _) =>
        {
            PinGlyph.IsVisible = false;
            CloseGlyph.IsVisible = true;
        };

        PointerExited += (_, _) =>
        {
            PinGlyph.IsVisible = true;
            CloseGlyph.IsVisible = false;
        };

        PointerPressed += OnPointerPressed;

        Opened += (_, _) =>
        {
            // 与 Snow-shot 一致：图钉会随屏幕一起被录进视频。
            Follow();
            _timer.Start();
        };

        Closed += (_, _) => _timer.Stop();
    }

    private void Follow()
    {
        if (!WindowPickerApi.Exists(_target))
        {
            Close();
            return;
        }

        // 最小化时图钉没地方贴，跟着藏起来
        if (WindowPickerApi.IsMinimized(_target))
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;

        var bounds = WindowPickerApi.GetBounds(_target);

        // 位置是物理像素：贴标题栏右侧，内缩一点避开最小化/关闭按钮
        Position = new PixelPoint(bounds.Right - 46, bounds.Top + 8);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;

        try
        {
            _onRemove();
        }
        finally
        {
            Close();
        }
    }
}
