using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    bool Gif = false,
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
    bool CursorShadow = false)
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
    {
        InitializeComponent();

        OutputDirectoryBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "recordings");

        _ = DetectCameraAsync();
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

        var fps = FpsBox.SelectedIndex == 1 ? 60 : 30;
        var cursor = CursorCheck.IsChecked == true;
        var gif = FormatBox.SelectedIndex == 1;

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
        var systemAudio = !gif && SystemAudio.IsChecked == true;
        var microphone = !gif && Microphone.IsChecked == true;

        Finished?.Invoke(new RecordingSetup(
            mode,
            crf,
            fps,
            cursor,
            systemAudio,
            microphone,
            gif,
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
            cursorShadow));

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
