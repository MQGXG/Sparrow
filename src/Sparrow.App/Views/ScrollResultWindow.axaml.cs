using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sparrow.App.Services;

namespace Sparrow.App.Views;

/// <summary>
/// 滚动截图的结果预览。
///
/// 复用普通截图的动作语义（<see cref="CaptureAction"/>），
/// 由 App 统一执行，这样「识别 / 复制 / 保存 / 固定」只有一套实现。
/// 右侧缩略图上的两个手柄可以裁掉长图头尾，动作执行前按行裁剪。
/// </summary>
public partial class ScrollResultWindow : Window
{
    /// <summary>手柄拖动的最小间隔（缩略图上的像素）。</summary>
    private const double MinimumHandleGap = 8;

    private Bitmap? _bitmap;
    private byte[]? _png;
    private int _sourceWidth;
    private int _sourceHeight;

    private double _trimTopFraction;
    private double _trimBottomFraction = 1;

    private bool _dragging;
    private bool _draggingTop;

    /// <summary>设计器需要的无参构造。</summary>
    public ScrollResultWindow()
    {
        InitializeComponent();
        Opened += (_, _) => WindowCaptureExclusion.Exclude(this);
    }

    public ScrollResultWindow(byte[] png) : this()
    {
        _png = png;
        var (width, height) = ImageSizeReader.Read(png);

        using var stream = new MemoryStream(png);
        var targetWidth = PreviewWidth(width, height);

        // 长图预览不按原尺寸解码：几万行的位图会直接吃掉几百 MB。
        // 导出的 PNG 仍是原尺寸，预览只是按总像素预算缩放。
        _bitmap = targetWidth > 0 && targetWidth < width
            ? Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.HighQuality)
            : new Bitmap(stream);

        Preview.Source = _bitmap;
        TrimOverview.Source = _bitmap;

        _sourceWidth = width > 0 ? width : _bitmap.PixelSize.Width;
        _sourceHeight = height > 0 ? height : _bitmap.PixelSize.Height;

        TrimTrack.SizeChanged += (_, _) => UpdateTrimVisual();
        UpdateTrimVisual();
    }

    /// <summary>用户选择的动作；关闭时为 <see cref="CaptureAction.None"/>。</summary>
    public CaptureAction Action { get; private set; }

    /// <summary>按当前裁剪范围生成的结果 PNG；没有裁剪时就是原图。</summary>
    public byte[]? OutputPng { get; private set; }

    private int TrimTopRows => Math.Clamp(
        (int)Math.Round(_trimTopFraction * _sourceHeight),
        0,
        Math.Max(0, _sourceHeight - 1));

    private int TrimBottomRows => Math.Clamp(
        _sourceHeight - (int)Math.Round(_trimBottomFraction * _sourceHeight),
        0,
        Math.Max(0, _sourceHeight - 1 - TrimTopRows));

    /// <summary>预览最多约 16 MP；长图按比例缩到预算内，避免整张原图进内存。</summary>
    private static int PreviewWidth(int width, int height)
    {
        const long budget = 16L * 1024 * 1024;

        if (width <= 0 || height <= 0)
            return 0;

        var pixels = (long)width * height;
        if (pixels <= budget)
            return width;

        return Math.Max(1, (int)Math.Round(width * Math.Sqrt((double)budget / pixels)));
    }

    protected override void OnClosed(EventArgs e)
    {
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.C when control:
                CloseWith(CaptureAction.CopyImage);
                e.Handled = true;
                return;
            case Key.S when !control:
                CloseWith(CaptureAction.Save);
                e.Handled = true;
                return;
            case Key.P when !control:
                CloseWith(CaptureAction.Pin);
                e.Handled = true;
                return;
            case Key.R when !control:
                CloseWith(CaptureAction.Recognize);
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    private void OnTrimPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var height = TrimTrack.Bounds.Height;
        if (height <= 0)
            return;

        var y = e.GetPosition(TrimTrack).Y;
        var top = _trimTopFraction * height;
        var bottom = _trimBottomFraction * height;

        _draggingTop = Math.Abs(y - top) <= Math.Abs(y - bottom);
        _dragging = true;
        e.Pointer.Capture(TrimTrack);
        ApplyTrimPointer(y, height);
        e.Handled = true;
    }

    private void OnTrimPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging)
            ApplyTrimPointer(e.GetPosition(TrimTrack).Y, TrimTrack.Bounds.Height);
    }

    private void OnTrimPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void OnTrimPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        _dragging = false;

    private void ApplyTrimPointer(double y, double height)
    {
        if (height <= 0)
            return;

        var fraction = Math.Clamp(y / height, 0, 1);
        var minGap = Math.Min(0.5, MinimumHandleGap / height);

        if (_draggingTop)
            _trimTopFraction = Math.Clamp(fraction, 0, _trimBottomFraction - minGap);
        else
            _trimBottomFraction = Math.Clamp(fraction, _trimTopFraction + minGap, 1);

        UpdateTrimVisual();
    }

    private void UpdateTrimVisual()
    {
        var height = TrimTrack.Bounds.Height;
        var width = TrimTrack.Bounds.Width;

        if (height > 0 && width > 0)
        {
            var top = _trimTopFraction * height;
            var bottom = _trimBottomFraction * height;

            LayoutShade(TrimTopShade, 0, Math.Max(0, top), width);
            LayoutShade(TrimBottomShade, bottom, Math.Max(0, height - bottom), width);
            LayoutHandle(TrimTopHandle, top, width, height);
            LayoutHandle(TrimBottomHandle, bottom, width, height);
        }

        var trimmedHeight = Math.Max(1, _sourceHeight - TrimTopRows - TrimBottomRows);
        var suffix = TrimTopRows > 0 || TrimBottomRows > 0
            ? $"（原 {_sourceHeight}，上裁 {TrimTopRows} / 下裁 {TrimBottomRows}）"
            : string.Empty;

        SizeText.Text = $"{_sourceWidth} × {trimmedHeight} px{suffix}";
    }

    private static void LayoutShade(Border shade, double top, double height, double width)
    {
        shade.Width = width;
        shade.Height = height;
        Canvas.SetLeft(shade, 0);
        Canvas.SetTop(shade, top);
    }

    private static void LayoutHandle(Border handle, double center, double width, double height)
    {
        handle.Width = width;
        Canvas.SetLeft(handle, 0);
        Canvas.SetTop(
            handle,
            Math.Clamp(center - handle.Height / 2, 0, Math.Max(0, height - handle.Height)));
    }

    private void OnRecognizeClick(object? sender, RoutedEventArgs e) => CloseWith(CaptureAction.Recognize);

    /// <summary>打开标注窗口，把结果合成回当前 PNG（尺寸不变，裁剪比例保持）。</summary>
    private async void OnAnnotateClick(object? sender, RoutedEventArgs e)
    {
        if (_png is not { Length: > 0 })
            return;

        var result = await ImageAnnotator.AnnotateAsync(this, _png);
        if (result is null)
            return;

        _png = result;

        using var stream = new MemoryStream(result);
        _bitmap?.Dispose();
        _bitmap = new Bitmap(stream);

        Preview.Source = _bitmap;
        TrimOverview.Source = _bitmap;

        var (width, height) = ImageSizeReader.Read(result);
        if (width > 0 && height > 0)
        {
            _sourceWidth = width;
            _sourceHeight = height;
        }

        UpdateTrimVisual();
    }

    private void OnCopyImageClick(object? sender, RoutedEventArgs e) => CloseWith(CaptureAction.CopyImage);

    private void OnSaveClick(object? sender, RoutedEventArgs e) => CloseWith(CaptureAction.Save);

    private void OnPinClick(object? sender, RoutedEventArgs e) => CloseWith(CaptureAction.Pin);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>无边框窗口：标题行拖动整个面板。</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void CloseWith(CaptureAction action)
    {
        Action = action;

        if (_png is { Length: > 0 })
            OutputPng = ScrollImageTrimmer.TrimPng(_png, TrimTopRows, TrimBottomRows);

        Close();
    }
}
