using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;

namespace Sparrow.App.Overlays;

/// <summary>
/// 摄像头画中画的预览/定位窗口。
///
/// 窗口本身被 WDA_EXCLUDEFROMCAPTURE 排除，不会进录屏；
/// 真正进视频的是 RecordingSession 里按这个窗口的屏幕矩形合成的那一份。
/// 用户可以拖动整个窗口、拖右下角自由缩放。
/// </summary>
internal sealed class CameraPreviewWindow : Window
{
    private const double MinPreviewWidth = 96;
    private const double MinPreviewHeight = 64;

    private readonly CameraCapture _camera;
    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly Border _grip;
    private readonly DispatcherTimer _timer;

    private PixelRect _region;
    private PixelRect _initialRect;
    private readonly bool _mirror;
    private Action<PixelRect> _layoutChanged;

    private WriteableBitmap? _bitmap;
    private long _lastFrameVersion = -1;
    private bool _adjusting;
    private bool _resizing;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private Point _resizeStartPoint;

    public CameraPreviewWindow(
        CameraCapture camera,
        PixelRect region,
        PixelRect initialRect,
        bool mirror,
        Action<PixelRect> layoutChanged)
    {
        _camera = camera;
        _region = NormalizeRegion(region);
        _initialRect = initialRect.Width > 0 && initialRect.Height > 0
            ? initialRect
            : CameraPlacement.DefaultScreenRect(_region);
        _mirror = mirror;
        _layoutChanged = layoutChanged;

        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        _image = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        if (_mirror)
        {
            _image.RenderTransformOrigin = RelativePoint.Center;
            _image.RenderTransform = new ScaleTransform(-1, 1);
        }

        _status = new TextBlock
        {
            Text = "摄像头准备中…",
            FontSize = 12,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };

        _grip = new Border
        {
            Width = 18,
            Height = 18,
            Margin = new Thickness(0, 0, 4, 4),
            Background = new SolidColorBrush(Color.Parse("#66000000")),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M2 10 L10 2 M5 10 L10 5 M8 10 L10 8"),
                Stroke = new SolidColorBrush(Color.Parse("#DDDDDD")),
                StrokeThickness = 1.2,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(4)
            }
        };

        var content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E6000000")),
            BorderBrush = new SolidColorBrush(Color.Parse("#66FFFFFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new Grid
            {
                Children = { _image, _status, _grip }
            }
        };

        Content = content;

        ApplyInitialBounds();

        content.PointerPressed += OnDragPressed;
        _grip.PointerPressed += OnGripPressed;
        _grip.PointerMoved += OnGripMoved;
        _grip.PointerReleased += OnGripReleased;

        PositionChanged += (_, _) => ClampToRegion();

        // 30fps 刷新。采集端有新帧时才真正拷贝；没有新帧就跳过，省掉 UI 线程的无效搬运。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => UpdatePreview();

        Opened += (_, _) =>
        {
            // 预览窗口只给用户看，不参与任何抓屏/录屏。
            WindowCaptureExclusion.Exclude(this);

            ClampToRegion();
            ReportLayout();
            _timer.Start();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _image.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
        };
    }

    /// <summary>会话创建后把布局目标切到会话；区域也更新为实际录制区域。</summary>
    public void Attach(RecordingSession session)
    {
        _layoutChanged = session.SetCameraScreenRect;
        _region = NormalizeRegion(session.ScreenRect);
        ClampToRegion();
        ReportLayout();
    }

    private void ApplyInitialBounds()
    {
        var rect = _initialRect;
        var scale = CurrentScale();

        if (rect.Width <= 0 || rect.Height <= 0)
        {
            var screen = Screens.Primary?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
            var width = Math.Max(MinPreviewWidth, screen.Width / 6.0);
            rect = new PixelRect(
                screen.Right - (int)width - 24,
                screen.Bottom - (int)(width * 9 / 16) - 24,
                (int)width,
                (int)(width * 9 / 16));
        }

        Position = new PixelPoint(rect.X, rect.Y);
        Width = Math.Max(MinPreviewWidth, rect.Width / scale);
        Height = Math.Max(MinPreviewHeight, rect.Height / scale);
    }

    private void UpdatePreview()
    {
        var version = _camera.FrameVersion;

        // 采集端没有新帧：保持当前画面，不再重复拷一遍。
        if (version == _lastFrameVersion && _bitmap is not null)
            return;

        if (!_camera.TryGetPreviewSize(out var width, out var height)
            && !_camera.TryGetFrameSize(out width, out height))
        {
            _status.IsVisible = true;
            _status.Text = _camera.Error ?? "摄像头准备中…";
            return;
        }

        _status.IsVisible = _camera.Error is not null;

        if (_bitmap is null
            || _bitmap.PixelSize.Width != width
            || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            _image.Source = _bitmap;
        }

        if (!_camera.CopyFrameTo(_bitmap))
            return;

        _lastFrameVersion = version;

        // Avalonia 不会因为 WriteableBitmap 的像素变化自动重绘（和 WPF 不同），
        // 必须显式让 Image 失效，否则预览会停在旧画面上，直到别的事件触发重绘。
        _image.InvalidateVisual();
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        BeginMoveDrag(e);
    }

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _resizing = true;
        _resizeStartPoint = e.GetPosition(this);
        _resizeStartWidth = Width;
        _resizeStartHeight = Height;
        e.Pointer.Capture(_grip);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing)
            return;

        var point = e.GetPosition(this);
        var width = _resizeStartWidth + (point.X - _resizeStartPoint.X);
        var height = _resizeStartHeight + (point.Y - _resizeStartPoint.Y);

        Width = Math.Max(MinPreviewWidth, width);
        Height = Math.Max(MinPreviewHeight, height);

        ClampToRegion();
        ReportLayout();
        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing)
            return;

        _resizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>窗口必须完整落在录制区域里，避免录到区域外或挡住半个屏幕。</summary>
    private void ClampToRegion()
    {
        if (_adjusting)
            return;

        _adjusting = true;

        try
        {
            var scale = CurrentScale();
            var regionWidth = Math.Max(MinPreviewWidth, _region.Width / scale);
            var regionHeight = Math.Max(MinPreviewHeight, _region.Height / scale);

            // 只按右下角缩放：位置不动，所以可用宽高要减去已经占掉的部分。
            var availableWidth = Math.Max(
                MinPreviewWidth,
                Math.Min(regionWidth, (_region.Right - Position.X) / scale));

            var availableHeight = Math.Max(
                MinPreviewHeight,
                Math.Min(regionHeight, (_region.Bottom - Position.Y) / scale));

            var width = Math.Clamp(Width, MinPreviewWidth, availableWidth);
            var height = Math.Clamp(Height, MinPreviewHeight, availableHeight);

            if (Math.Abs(width - Width) > 0.01)
                Width = width;

            if (Math.Abs(height - Height) > 0.01)
                Height = height;

            var maxX = _region.Right - (int)Math.Ceiling(width * scale);
            var maxY = _region.Bottom - (int)Math.Ceiling(height * scale);

            var x = Math.Clamp(Position.X, _region.X, Math.Max(_region.X, maxX));
            var y = Math.Clamp(Position.Y, _region.Y, Math.Max(_region.Y, maxY));

            if (x != Position.X || y != Position.Y)
                Position = new PixelPoint(x, y);

            ReportLayout();
        }
        finally
        {
            _adjusting = false;
        }
    }

    private void ReportLayout()
    {
        if (_region.Width <= 0 || _region.Height <= 0)
            return;

        var scale = CurrentScale();
        var rect = new PixelRect(
            Position.X,
            Position.Y,
            Math.Max(1, (int)Math.Round(Width * scale)),
            Math.Max(1, (int)Math.Round(Height * scale)));

        _layoutChanged(rect);
    }

    private double CurrentScale()
    {
        try
        {
            var point = new PixelPoint(Position.X + 8, Position.Y + 8);
            var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
            var scale = screen?.Scaling ?? RenderScaling;

            return scale > 0 ? scale : 1;
        }
        catch
        {
            return 1;
        }
    }

    private PixelRect NormalizeRegion(PixelRect screenRect)
    {
        if (screenRect.Width > 0 && screenRect.Height > 0)
            return screenRect;

        return Screens.Primary?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
    }
}
