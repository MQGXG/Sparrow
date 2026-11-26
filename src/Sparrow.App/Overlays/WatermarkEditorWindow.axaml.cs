using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;

namespace Sparrow.App.Overlays;

/// <summary>
/// 水印编辑覆盖层：单个图文水印块，拖动移动、滚轮/滑块缩放、双击复位。
/// 结果用相对坐标保存，录制时按视频尺寸换算。
/// </summary>
public partial class WatermarkEditorWindow : Window
{
    private const double BaseImageHeight = 48;
    private const double BaseFontSize = 24;

    private WatermarkLayout _layout;
    private string _imagePath = "";
    private bool _dragging;
    private Point _dragStart;
    private double _startLeft;
    private double _startTop;

    public WatermarkEditorWindow() : this(WatermarkLayout.Default)
    {
    }

    public WatermarkEditorWindow(WatermarkLayout layout)
    {
        InitializeComponent();

        _layout = layout;
        ApplyLayout(layout);

        Opened += (_, _) =>
        {
            // 编辑水印时不能把编辑层录进视频（如果用户在录制中打开编辑器）。
            WindowCaptureExclusion.Exclude(this);
            Dispatcher.UIThread.Post(PlaceBlock, DispatcherPriority.Loaded);
        };
    }

    /// <summary>
    /// 替换水印位图：先断开 Image.Source 引用，再释放旧位图。
    /// Avalonia 的 Bitmap 是 native 内存，GC 看不见真实占用，只置 null 会让
    /// 每张 4K 图留在 finalizer 队列里，反复换图 / 开关窗口工作集只涨不跌。
    /// </summary>
    private void SetWatermarkImage(Bitmap? bitmap)
    {
        var previous = WatermarkImage.Source;
        WatermarkImage.Source = bitmap;
        WatermarkImage.IsVisible = bitmap is not null;

        if (previous is IDisposable disposable && !ReferenceEquals(previous, bitmap))
            disposable.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关窗时立刻释放当前水印位图，不等 finalizer。
        SetWatermarkImage(null);
        base.OnClosed(e);
    }

    private void ApplyLayout(WatermarkLayout layout)
    {
        _imagePath = layout.ImagePath;

        WatermarkTextBox.Text = layout.Text;
        WatermarkText.Text = layout.Text;
        OpacitySlider.Value = Math.Clamp(layout.Opacity, 0.1, 1);
        WatermarkBlock.Opacity = OpacitySlider.Value;

        if (!string.IsNullOrWhiteSpace(layout.ImagePath) && File.Exists(layout.ImagePath))
        {
            try
            {
                SetWatermarkImage(new Bitmap(layout.ImagePath));
            }
            catch
            {
                _imagePath = "";
                SetWatermarkImage(null);
            }
        }
        else
        {
            _imagePath = "";
            SetWatermarkImage(null);
        }
    }

    private void PlaceBlock()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var blockWidth = WatermarkBlock.Bounds.Width;
        var blockHeight = WatermarkBlock.Bounds.Height;

        var left = _layout.CenterX * width - blockWidth / 2;
        var top = _layout.CenterY * height - blockHeight / 2;

        Canvas.SetLeft(WatermarkBlock, Math.Clamp(left, 0, Math.Max(0, width - blockWidth)));
        Canvas.SetTop(WatermarkBlock, Math.Clamp(top, 0, Math.Max(0, height - blockHeight)));
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e) =>
        WatermarkText.Text = WatermarkTextBox.Text ?? "";

    private void OnScaleChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var scale = ScaleSlider.Value;

        WatermarkImage.Height = BaseImageHeight * scale;
        WatermarkText.FontSize = BaseFontSize * scale;
    }

    private void OnOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e) =>
        WatermarkBlock.Opacity = OpacitySlider.Value;

    private void OnBlockPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(WatermarkBlock).Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        _dragStart = e.GetPosition(EditorCanvas);
        _startLeft = Canvas.GetLeft(WatermarkBlock);
        _startTop = Canvas.GetTop(WatermarkBlock);
        e.Pointer.Capture(WatermarkBlock);
    }

    private void OnBlockMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;

        var position = e.GetPosition(EditorCanvas);
        var left = _startLeft + (position.X - _dragStart.X);
        var top = _startTop + (position.Y - _dragStart.Y);

        Canvas.SetLeft(
            WatermarkBlock,
            Math.Clamp(left, 0, Math.Max(0, Bounds.Width - WatermarkBlock.Bounds.Width)));

        Canvas.SetTop(
            WatermarkBlock,
            Math.Clamp(top, 0, Math.Max(0, Bounds.Height - WatermarkBlock.Bounds.Height)));
    }

    private void OnBlockReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void OnBlockDoubleTapped(object? sender, TappedEventArgs e) =>
        ApplyLayout(WatermarkLayout.Default);

    private void OnBlockWheel(object? sender, PointerWheelEventArgs e)
    {
        ScaleSlider.Value = Math.Clamp(
            ScaleSlider.Value + e.Delta.Y * 0.1,
            ScaleSlider.Minimum,
            ScaleSlider.Maximum);

        e.Handled = true;
    }

    private async void OnPickImage(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择水印图片",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });

        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();

        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            SetWatermarkImage(new Bitmap(path));
            _imagePath = path;
        }
        catch
        {
            // 不支持的图片格式：保持原状态。
        }
    }

    private void OnClearImage(object? sender, RoutedEventArgs e)
    {
        _imagePath = "";
        SetWatermarkImage(null);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        var blockWidth = WatermarkBlock.Bounds.Width;
        var blockHeight = WatermarkBlock.Bounds.Height;

        var centerX = (Canvas.GetLeft(WatermarkBlock) + blockWidth / 2) / width;
        var centerY = (Canvas.GetTop(WatermarkBlock) + blockHeight / 2) / height;
        var scale = blockHeight / height;

        var result = new WatermarkLayout
        {
            Text = WatermarkTextBox.Text?.Trim() ?? "",
            ImagePath = _imagePath,
            CenterX = Math.Clamp(centerX, 0, 1),
            CenterY = Math.Clamp(centerY, 0, 1),
            Scale = Math.Clamp(scale, 0.02, 0.35),
            TextScale = 1,
            Opacity = OpacitySlider.Value
        };

        Close(result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
