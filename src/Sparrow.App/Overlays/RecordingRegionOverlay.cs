using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Sparrow.App.Overlays;

/// <summary>
/// 录制中的选区红框：全屏透明覆盖层。
///
/// 红框画在录制区域**外侧**（与选区留 2 DIP 空隙），所以：
/// - 屏幕上、截图里都能看到红框（对齐 Snow-shot）；
/// - 录制区域本身不包含红框，视频里天然没有。
///
/// Avalonia 的 DirectComposition 窗口不响应 WS_EX_TRANSPARENT 的点击穿透，
/// 这里用 SetWindowRgn 把窗口区域限制成红框那一圈：
/// 选区内外都能点到下层窗口，只有几像素宽的红框会拦截鼠标。
/// </summary>
internal sealed class RecordingRegionOverlay : Window
{
    private const int RgnOr = 2;

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    /// <summary>红框与选区之间的空隙（DIP）。</summary>
    private const double FrameGap = 2;

    /// <summary>红框粗细（DIP）。</summary>
    private const double FrameThickness = 2;

    private readonly PixelRect _region;

    /// <summary>红框外沿的物理像素矩形（窗口坐标系），点击穿透按它裁剪。</summary>
    private PixelRect _frameRect;

    public RecordingRegionOverlay(PixelRect region)
    {
        _region = region;

        WindowDecorations = WindowDecorations.None;
        WindowState = WindowState.FullScreen;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        IsHitTestVisible = false;

        Opened += (_, _) =>
        {
            BuildFrame();
            SetClickThrough();

            // Avalonia 进入 FullScreen 后可能重建窗口，延迟再确认一次区域。
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                BuildFrame();
                SetClickThrough();
            };
            timer.Start();
        };
    }

    private void BuildFrame()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;

        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var bounds = screen.Bounds;

        var left = (_region.X - bounds.X) / scale;
        var top = (_region.Y - bounds.Y) / scale;
        var width = _region.Width / scale;
        var height = _region.Height / scale;

        // 红框整体外扩：Border 的描边画在自身边界内侧，
        // 外扩 (空隙 + 粗细) 后描边正好落在选区之外，不会进视频。
        var offset = FrameGap + FrameThickness;

        var offsetPx = (int)Math.Round(offset * scale);

        _frameRect = new PixelRect(
            _region.X - bounds.X - offsetPx,
            _region.Y - bounds.Y - offsetPx,
            _region.Width + offsetPx * 2,
            _region.Height + offsetPx * 2);

        var canvas = new Canvas();

        var frame = new Border
        {
            Width = width + offset * 2,
            Height = height + offset * 2,
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 64, 64)),
            BorderThickness = new Thickness(FrameThickness),
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(frame, left - offset);
        Canvas.SetTop(frame, top - offset);
        canvas.Children.Add(frame);

        Content = canvas;
    }

    private void SetClickThrough()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            App.Log.Warn("[record] 选区覆盖层没有窗口句柄，点击穿透未设置");
            return;
        }

        // 覆盖层只是画红框：不抢前台、不出现在 Alt+Tab，
        // 否则录制开始时焦点会被它拿走，下层应用第一次点击只用来激活窗口。
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(
            hwnd,
            GwlExStyle,
            new IntPtr(exStyle | WsExNoActivate | WsExToolWindow));

        var frame = _frameRect;

        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        try
        {
            var thickness = Math.Max(2, (int)Math.Round(FrameThickness * ScreenScale()));

            var region = CreateRectRgn(0, 0, 0, 0);
            var pieces = new List<IntPtr>();

            void Add(int left, int top, int right, int bottom)
            {
                var piece = CreateRectRgn(left, top, right, bottom);
                pieces.Add(piece);
                CombineRgn(region, region, piece, RgnOr);
            }

            Add(frame.X, frame.Y, frame.Right, frame.Y + thickness);
            Add(frame.X, frame.Bottom - thickness, frame.Right, frame.Bottom);
            Add(frame.X, frame.Y, frame.X + thickness, frame.Bottom);
            Add(frame.Right - thickness, frame.Y, frame.Right, frame.Bottom);

            var result = SetWindowRgn(hwnd, region, true);

            if (result == 0)
                DeleteObject(region);

            foreach (var piece in pieces)
                DeleteObject(piece);

            App.Log.Info(
                $"[record] 选区覆盖层区域：hwnd=0x{hwnd:X}，" +
                $"frame={frame.X},{frame.Y},{frame.Width}x{frame.Height}，result={result}");
        }
        catch (Exception ex)
        {
            App.Log.Error("[record] 选区覆盖层设置点击穿透失败", ex);
        }
    }

    private double ScreenScale()
    {
        var scale = Screens.Primary?.Scaling ?? 1.0;
        return scale <= 0 ? 1.0 : scale;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
