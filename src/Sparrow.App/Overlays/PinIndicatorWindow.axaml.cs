using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Sparrow.App.Overlays;

/// <summary>
/// 被置顶窗口标题栏旁的图钉：50ms 跟随一次，hover 变矢量 ✕，点击触发取消置顶。
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

        // 明确反馈：告诉用户到底哪个窗口被钉住了，而不是只留下一个看不懂的图钉
        var title = WindowPickerApi.GetTitle(_target);
        ToolTip.SetTip(this, string.IsNullOrWhiteSpace(title)
            ? "已置顶 · 点击取消"
            : $"已置顶：{title} · 点击取消");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Follow();

        PointerEntered += (_, _) =>
        {
            PinGlyph.IsVisible = false;
            CloseGlyph.IsVisible = true;
            StatusRing.IsVisible = false;
            Plate.Classes.Add("removing");
        };

        PointerExited += (_, _) =>
        {
            PinGlyph.IsVisible = true;
            CloseGlyph.IsVisible = false;
            StatusRing.IsVisible = true;
            Plate.Classes.Remove("removing");
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

        // 位置是物理像素：34px 徽章贴标题栏右侧，留 18px 避开最小化 / 关闭按钮，
        // 垂直方向在标准标题栏高度里居中。
        Position = new PixelPoint(bounds.Right - 52, bounds.Top + 6);
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
