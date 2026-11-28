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
            Plate.Classes.Add("removing");
        };

        PointerExited += (_, _) =>
        {
            PinGlyph.IsVisible = true;
            CloseGlyph.IsVisible = false;
            Plate.Classes.Remove("removing");
        };

        PointerPressed += OnPointerPressed;

        Opened += (_, _) =>
        {
            var handle = TryGetPlatformHandle()?.Handle ?? 0;

            if (handle != 0)
            {
                // 关键：让图钉成为目标窗口的 owned window。
                // 只设 Topmost 是不够的——目标窗口一激活就会盖住图钉。
                WindowPickerApi.SetOwner(handle, _target);
                WindowPickerApi.MarkToolWindow(handle);
            }

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

        // 最小化 / 被隐藏时图钉没地方贴，跟着藏起来
        if (WindowPickerApi.IsMinimized(_target) || !WindowPickerApi.IsVisible(_target))
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;

        // 别的程序可能清掉目标的 TOPMOST；只要图钉还在，就补回来
        if (!WindowPickerApi.IsTopMost(_target))
            WindowPickerApi.SetTopMost(_target, true);

        var bounds = WindowPickerApi.GetBounds(_target);

        // 标题栏右侧：系统按钮左边。按系统按钮数 + 当前 DPI 算，避免压住关闭按钮。
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        var buttonWidth = (int)Math.Round(WindowPickerApi.GetCaptionButtonWidth() * scale);
        var buttons = WindowPickerApi.GetCaptionButtonCount(_target);
        var badge = (int)Math.Round(32 * scale);
        var gap = (int)Math.Round(8 * scale);

        Position = new PixelPoint(
            bounds.Right - buttons * buttonWidth - badge - gap,
            bounds.Top + (int)Math.Round(6 * scale));
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
