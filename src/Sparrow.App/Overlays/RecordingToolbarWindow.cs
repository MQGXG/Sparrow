using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Sparrow.App.Services;

namespace Sparrow.App.Overlays;

/// <summary>
/// 录制期间承载截图工具条的独立窗口。
///
/// 工具条和标注画布必须分属两个窗口：标注要被录进视频，工具条不能。
/// WDA_EXCLUDEFROMCAPTURE 只能按整个顶层窗口排除，所以这里把 ActionBar
/// 和它的浮层从 CaptureWindow 搬到这个窗口，再对窗口做捕获排除。
/// 窗口区域只保留工具条/浮层本身，其余位置不参与命中测试。
/// </summary>
internal sealed class RecordingToolbarWindow : Window
{
    private const int RgnOr = 2;

    private readonly Canvas _canvas = new();
    private readonly List<Control> _controls;
    private readonly double _scale;
    private readonly DispatcherTimer _timer;
    private List<PixelRect> _region = [];
    private IntPtr _regionHandle;
    private bool _dragging;
    private Control? _dragSource;
    private Point _dragStart;
    private double _dragStartLeft;
    private double _dragStartTop;

    public RecordingToolbarWindow(
        IReadOnlyList<Control> controls,
        PixelPoint position,
        double scaling,
        double width,
        double height)
    {
        _controls = controls.ToList();
        _scale = scaling > 0 ? scaling : 1;

        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        Position = position;
        Width = width;
        Height = height;
        Content = _canvas;

        // 工具条的样式原本在 CaptureWindow.Window.Styles 里；搬到独立窗口后
        // 需要把这个共享样式文件也挂到本窗口，否则按钮图标/背景全部丢失。
        Styles.Add(new StyleInclude(new Uri("avares://Sparrow.App/"))
        {
            Source = new Uri("avares://Sparrow.App/Styles/CaptureToolbar.axaml")
        });

        // CaptureWindow.Window.Resources 里的紧凑滑块参数不会跟着控件搬过来。
        Resources["SliderHorizontalHeight"] = 16d;
        Resources["SliderHorizontalThumbWidth"] = 12d;
        Resources["SliderHorizontalThumbHeight"] = 12d;
        Resources["SliderThumbCornerRadius"] = new CornerRadius(6);
        Resources["SliderTrackThemeHeight"] = 3d;
        Resources["SliderPreContentMargin"] = new GridLength(4);
        Resources["SliderPostContentMargin"] = new GridLength(4);

        foreach (var control in _controls)
            _canvas.Children.Add(control);

        Opened += (_, _) =>
        {
            // 工具条只给用户看，不进录屏；标注仍在 CaptureWindow 里正常被捕获。
            WindowCaptureExclusion.Exclude(this);
            UpdateRegion();
        };

        LayoutUpdated += (_, _) => UpdateRegion();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => UpdateRegion();

        Opened += (_, _) => _timer.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _region = [];
            _regionHandle = IntPtr.Zero;
        };
    }

    /// <summary>工具条的坐标基准；浮层定位要跟着它走。</summary>
    public Canvas Canvas => _canvas;

    /// <summary>
    /// 从工具条拖拽把手开始拖动。工具条窗口本身是铺满虚拟屏幕的，
    /// 所以这里移动的是 Canvas.Left/Top，不是窗口位置。
    /// </summary>
    public void BeginToolbarDrag(PointerPressedEventArgs e)
    {
        var toolbar = _controls.FirstOrDefault();

        if (toolbar is null)
            return;

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragStartLeft = Canvas.GetLeft(toolbar);
        _dragStartTop = Canvas.GetTop(toolbar);

        if (double.IsNaN(_dragStartLeft))
            _dragStartLeft = 0;

        if (double.IsNaN(_dragStartTop))
            _dragStartTop = 0;

        _dragSource = e.Source as Control;

        if (_dragSource is not null)
        {
            _dragSource.PointerMoved += OnDragMoved;
            _dragSource.PointerReleased += OnDragReleased;
            e.Pointer.Capture(_dragSource);
        }
    }

    private void OnDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;

        var toolbar = _controls.FirstOrDefault();

        if (toolbar is null)
            return;

        var point = e.GetPosition(this);
        var size = toolbar.Bounds.Width > 0 && toolbar.Bounds.Height > 0
            ? toolbar.Bounds.Size
            : toolbar.DesiredSize;

        var maxLeft = Math.Max(0, Width - size.Width);
        var maxTop = Math.Max(0, Height - size.Height);

        var left = Math.Clamp(_dragStartLeft + (point.X - _dragStart.X), 0, maxLeft);
        var top = Math.Clamp(_dragStartTop + (point.Y - _dragStart.Y), 0, maxTop);

        Canvas.SetLeft(toolbar, left);
        Canvas.SetTop(toolbar, top);
        UpdateRegion();
        e.Handled = true;
    }

    private void OnDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;

        if (_dragSource is not null)
        {
            _dragSource.PointerMoved -= OnDragMoved;
            _dragSource.PointerReleased -= OnDragReleased;
            _dragSource = null;
        }

        e.Pointer.Capture(null);
        UpdateRegion();
        e.Handled = true;
    }

    private void UpdateRegion()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        var rects = new List<PixelRect>();

        foreach (var control in _controls)
        {
            if (!control.IsVisible)
                continue;

            if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                continue;

            if (control.TranslatePoint(new Point(0, 0), _canvas) is not { } origin)
                continue;

            var rect = ToWindowPixels(new Rect(origin, control.Bounds.Size));

            if (rect.Width > 0 && rect.Height > 0)
                rects.Add(rect);
        }

        if (hwnd == _regionHandle
            && rects.Count == _region.Count
            && rects.SequenceEqual(_region))
            return;

        var region = CreateRectRgn(0, 0, 0, 0);

        foreach (var rect in rects)
        {
            var piece = CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
            CombineRgn(region, region, piece, RgnOr);
            DeleteObject(piece);
        }

        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
            _region = [];
            _regionHandle = IntPtr.Zero;
            return;
        }

        _region = rects;
        _regionHandle = hwnd;
    }

    private PixelRect ToWindowPixels(Rect rect)
    {
        if (double.IsNaN(Width) || double.IsNaN(Height) || Width <= 0 || Height <= 0)
            return default;

        var maxRight = (int)Math.Ceiling(Width * _scale);
        var maxBottom = (int)Math.Ceiling(Height * _scale);

        var left = Math.Clamp((int)Math.Floor(rect.X * _scale), 0, maxRight);
        var top = Math.Clamp((int)Math.Floor(rect.Y * _scale), 0, maxBottom);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right * _scale), left, maxRight);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom * _scale), top, maxBottom);

        return new PixelRect(left, top, right - left, bottom - top);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
