using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;
using Windows.Devices.Enumeration;

namespace Sparrow.App.Overlays;

/// <summary>录制模式。</summary>
public enum RecordingMode
{
    /// <summary>先打开截图窗口拖选区域，再开始录制。</summary>
    Region,

    /// <summary>拾取一个窗口，直接录该窗口内容。</summary>
    Window,

    /// <summary>直接录主屏全屏。</summary>
    Fullscreen
}

/// <summary>启动面板的录制参数。</summary>
public sealed record RecordingSetup(
    RecordingMode Mode,
    int Crf,
    int FramesPerSecond,
    bool IncludeCursor,
    bool SystemAudio = false,
    bool Microphone = false,
    string Format = "mp4",
    int GifFps = 15,
    int GifWidth = 720,
    WatermarkLayout? Watermark = null,
    int CountdownSeconds = 0,
    int MaxDurationSeconds = 0,
    string Preset = "veryfast",
    string OutputDirectory = "",
    string BaseName = "Sparrow",
    int GopSeconds = 0,
    int CursorRadius = 22,
    uint CursorColor = 0xFFFFD640,
    byte CursorOpacity = 210,
    bool CursorShadow = false,
    string Encoder = "auto")
{
    public static RecordingSetup Default { get; } =
        new(RecordingMode.Fullscreen, 23, 30, true);
}

/// <summary>
/// 屏幕录制的启动设置面板（参考 Snow-shot 的录屏弹窗）：
/// 区域 / 全屏、格式、质量、帧率、鼠标、音频（占位）。
/// </summary>
public partial class RecordingSetupWindow : Window
{
    /// <summary>点了开始或取消：开始给参数，取消给 null。</summary>
    public event Action<RecordingSetup?>? Finished;

    private WatermarkLayout? _watermark;

    public RecordingSetupWindow()
        : this(null)
    {
    }

    public RecordingSetupWindow(string? savedEncoder, bool regionOnly = false)
    {
        InitializeComponent();

        OutputDirectoryBox.Text = SparrowPaths.Recordings;

        // 从截图工具条打开时区域已经由选区决定，不再给窗口/全屏选项。
        if (regionOnly)
        {
            RegionMode.IsChecked = true;
            WindowMode.IsVisible = false;
            FullscreenMode.IsVisible = false;
        }

        _ = DetectCameraAsync();
        _ = LoadEncodersAsync(savedEncoder);
        UpdateFormatVisibility();
    }

    /// <summary>无边框窗口：拖标题栏移动窗口。</summary>
    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>GIF / APNG 才需要动画帧率与尺寸；视频格式下隐藏。</summary>
    private void OnFormatChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateFormatVisibility();

    private void UpdateFormatVisibility()
    {
        // 初始化阶段控件可能还没赋值，等构造末尾再统一刷新一次。
        if (AnimationOptions is null || AudioSection is null)
            return;

        var gifLike = FormatBox.SelectedIndex is 3 or 4;
        AnimationOptions.IsVisible = gifLike;
        AudioSection.IsVisible = !gifLike;
    }

    /// <summary>
    /// 填充编码器下拉：自动 + 本机可用的硬编 + 软件编码。
    /// 探测放在后台线程，面板先显示“自动”，避免打开时卡住。
    /// </summary>
    private async Task LoadEncodersAsync(string? savedEncoder)
    {
        EncoderBox.Items.Clear();
        EncoderBox.Items.Add(new ComboBoxItem { Content = "自动（推荐）", Tag = "auto" });
        EncoderBox.SelectedIndex = 0;

        var ffmpeg = FfmpegLocator.Find();

        if (ffmpeg is null)
            return;

        IReadOnlyDictionary<string, bool> availability;

        try
        {
            availability = await Task.Run(() => VideoEncoderSelector.GetAvailability(ffmpeg));
        }
        catch
        {
            return;
        }

        var options = new (string Name, string Label)[]
        {
            ("h264_qsv", "硬件 · Intel QSV"),
            ("h264_nvenc", "硬件 · NVIDIA NVENC"),
            ("h264_amf", "硬件 · AMD AMF"),
            ("h264_mf", "硬件 · Media Foundation"),
            ("libx264", "软件 · x264")
        };

        foreach (var (name, label) in options)
        {
            var available = availability.TryGetValue(name, out var value) && value;

            EncoderBox.Items.Add(new ComboBoxItem
            {
                Content = available ? label : $"{label}（不可用）",
                Tag = name,
                IsEnabled = available
            });
        }

        if (string.IsNullOrWhiteSpace(savedEncoder) || savedEncoder == "auto")
            return;

        for (var index = 0; index < EncoderBox.Items.Count; index++)
        {
            if (EncoderBox.Items[index] is ComboBoxItem item
                && item.IsEnabled
                && string.Equals(item.Tag as string, savedEncoder, StringComparison.OrdinalIgnoreCase))
            {
                EncoderBox.SelectedIndex = index;
                return;
            }
        }
    }

    private async void OnPickOutputDirectory(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择录制保存目录",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        var path = folders[0].TryGetLocalPath();

        if (!string.IsNullOrEmpty(path))
            OutputDirectoryBox.Text = path;
    }

    private async void OnEditWatermarkClick(object? sender, RoutedEventArgs e)
    {
        var editor = new WatermarkEditorWindow(_watermark ?? WatermarkLayout.Default);
        var result = await editor.ShowDialog<WatermarkLayout?>(this);

        if (result is null)
            return;

        _watermark = result.IsEmpty ? null : result;
        WatermarkSummary.Text = result.IsEmpty ? "未设置" : DescribeWatermark(result);
    }

    private static string DescribeWatermark(WatermarkLayout layout)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(layout.ImagePath))
            parts.Add("图片");

        if (!string.IsNullOrWhiteSpace(layout.Text))
            parts.Add($"“{layout.Text}”");

        return parts.Count == 0 ? "未设置" : string.Join(" + ", parts);
    }

    /// <summary>检测摄像头；没有设备时保持禁用，避免用户勾了却没有画面。</summary>
    private async Task DetectCameraAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            if (devices.Count == 0)
            {
                CameraCheck.Content = "摄像头（未检测到）";
                return;
            }

            CameraCheck.Content = $"摄像头（{devices.Count}）";
            CameraCheck.IsEnabled = true;
            CameraCheck.Opacity = 1;
        }
        catch
        {
            CameraCheck.Content = "摄像头（不可用）";
        }
    }

    private void OnStartClick(object? sender, RoutedEventArgs e)
    {
        var mode = RegionMode.IsChecked == true
            ? RecordingMode.Region
            : WindowMode.IsChecked == true
                ? RecordingMode.Window
                : RecordingMode.Fullscreen;

        // 原画 16 / 超清 18 / 高清 20 / 清晰 23 / 普清 26 / 一般 30。
        var crf = QualityBox.SelectedIndex switch
        {
            0 => 16,
            1 => 18,
            2 => 20,
            4 => 26,
            5 => 30,
            _ => 23
        };

        var preset = PresetBox.SelectedIndex switch
        {
            0 => "ultrafast",
            1 => "superfast",
            3 => "faster",
            4 => "medium",
            _ => "veryfast"
        };

        var countdown = CountdownBox.SelectedIndex switch
        {
            1 => 3,
            2 => 5,
            3 => 10,
            _ => 0
        };

        var maxMinutes = int.TryParse(DurationBox.Text, out var minutes) ? minutes : 0;
        var gopSeconds = int.TryParse(GopBox.Text, out var gop) ? gop : 0;
        var cursorRadius = int.TryParse(CursorRadiusBox.Text, out var radius) ? radius : 22;
        var cursorShadow = CursorShadowCheck.IsChecked == true;
        var cursorOpacity = (byte)Math.Clamp(CursorOpacitySlider.Value, 0, 255);

        var cursorColor = CursorColorBox.SelectedIndex switch
        {
            0 => 0xFFFFD640u,
            1 => 0xFFFFFFFFu,
            2 => 0xFFFF4D4Fu,
            3 => 0xFF52C41Au,
            4 => 0xFF4C9AFFu,
            _ => 0xFFFFD640u
        };

        var outputDirectory = OutputDirectoryBox.Text?.Trim() ?? "";
        var baseName = BaseNameBox.Text?.Trim() ?? "Sparrow";

        var fps = FpsBox.SelectedIndex switch
        {
            1 => 60,
            2 => 90,
            3 => 120,
            4 => 144,
            _ => 30
        };
        var cursor = CursorCheck.IsChecked == true;

        var format = FormatBox.SelectedIndex switch
        {
            1 => "mkv",
            2 => "mov",
            3 => "gif",
            4 => "apng",
            _ => "mp4"
        };

        // GIF / APNG 是无声动画，勾了音频也不采。
        var supportsAudio = format is "mp4" or "mkv" or "mov";

        var gifFps = GifFpsBox.SelectedIndex switch
        {
            0 => 10,
            2 => 20,
            _ => 15
        };

        var gifWidth = GifWidthBox.SelectedIndex switch
        {
            0 => 480,
            2 => 1080,
            _ => 720
        };

        var watermark = _watermark;

        // GIF 没有音轨，勾了也不采。
        var systemAudio = supportsAudio && SystemAudio.IsChecked == true;
        var microphone = supportsAudio && Microphone.IsChecked == true;

        var encoder = (EncoderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

        Finished?.Invoke(new RecordingSetup(
            mode,
            crf,
            fps,
            cursor,
            systemAudio,
            microphone,
            format,
            gifFps,
            gifWidth,
            watermark,
            countdown,
            maxMinutes * 60,
            preset,
            outputDirectory,
            baseName,
            gopSeconds,
            cursorRadius,
            cursorColor,
            cursorOpacity,
            cursorShadow,
            encoder));

        Close();
    }

    /// <summary>把所有选项恢复成默认值（不关闭面板）。</summary>
    private void OnRestoreDefaults(object? sender, RoutedEventArgs e)
    {
        RegionMode.IsChecked = true;
        WindowMode.IsChecked = false;
        FullscreenMode.IsChecked = false;

        FormatBox.SelectedIndex = 0;
        QualityBox.SelectedIndex = 3;
        FpsBox.SelectedIndex = 0;
        PresetBox.SelectedIndex = 2;
        CountdownBox.SelectedIndex = 0;
        DurationBox.Text = "0";
        GopBox.Text = "0";

        CursorCheck.IsChecked = true;
        CursorRadiusBox.Text = "22";
        CursorColorBox.SelectedIndex = 0;
        CursorOpacitySlider.Value = 210;
        CursorShadowCheck.IsChecked = false;

        GifFpsBox.SelectedIndex = 1;
        GifWidthBox.SelectedIndex = 1;
        BaseNameBox.Text = "Sparrow";

        EncoderBox.SelectedIndex = 0;

        SystemAudio.IsChecked = false;
        Microphone.IsChecked = false;
        CameraCheck.IsChecked = false;

        _watermark = null;
        WatermarkSummary.Text = "未设置";
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Finished?.Invoke(null);
        Close();
    }
}
