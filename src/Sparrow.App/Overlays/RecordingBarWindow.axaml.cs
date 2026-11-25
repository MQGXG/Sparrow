using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;

namespace Sparrow.App.Overlays;

/// <summary>
/// 屏幕录制控制条（参考 Snow-shot 的录屏工具条布局）：
/// 拖拽把手、红点停止、暂停/继续、计时、麦克风/系统声（本版占位）、
/// 打开目录、取消、保存。
///
/// 录制会话本身在宿主后台线程跑，这里只做状态展示与控制。
/// </summary>
public partial class RecordingBarWindow : Window
{
    private readonly RecordingSession? _session;
    private readonly PixelRect? _region;
    private readonly DispatcherTimer _timer;
    private bool _closed;
    private bool _copyOnFinish;

    /// <summary>结束时回调：成功给文件路径（取消/失败为 null），第二个参数是是否已复制到剪贴板。</summary>
    public event Action<string?, bool>? Finished;

    /// <summary>XAML 设计器需要无参构造。</summary>
    public RecordingBarWindow() : this(null, null)
    {
    }

    internal RecordingBarWindow(RecordingSession? session, PixelRect? region = null)
    {
        InitializeComponent();

        _session = session;
        _region = region;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => UpdateElapsed();

        if (_session is not null)
        {
            _session.StateChanged += OnStateChanged;

            MicrophoneGlyph.Opacity = _session.HasMicrophone ? 1 : 0.3;
            SystemAudioGlyph.Opacity = _session.HasSystemAudio ? 1 : 0.3;
        }

        Opened += (_, _) =>
        {
            // 控制条属于录制工具本身：屏幕上可见，但保存的视频里不出现。
            WindowCaptureExclusion.Exclude(this);
            PlaceNearRegion();
            UpdateElapsed();
            _timer.Start();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            e.Handled = true;
            OnCancelClick(this, new RoutedEventArgs());
        };

        Closed += (_, _) =>
        {
            _timer.Stop();

            if (_session is not null)
                _session.StateChanged -= OnStateChanged;
        };
    }

    /// <summary>
    /// 控制条位置：区域录制贴在选区下方（放不下就上方，再不行贴屏幕底部），
    /// 全屏录制贴主屏底部居中。
    /// </summary>
    private void PlaceNearRegion()
    {
        var screen = Screens.Primary;
        if (screen is null)
            return;

        var bounds = screen.Bounds;
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);

        if (_region is { } region && region.Width > 0 && region.Height > 0)
        {
            var x = region.X + (region.Width - width) / 2;
            var y = region.Y + region.Height + (int)(12 * scale);

            // 选区下方放不下就放到上方
            if (y + height > bounds.Bottom)
                y = region.Y - height - (int)(12 * scale);

            // 上方也放不下（选区几乎全屏）就贴屏幕底部
            if (y < bounds.Y)
                y = bounds.Bottom - height - (int)(72 * scale);

            Position = new PixelPoint(
                Math.Clamp(x, bounds.X + 8, bounds.Right - width - 8),
                Math.Clamp(y, bounds.Y + 8, bounds.Bottom - height - 8));
            return;
        }

        // 全屏录制：贴主屏底部居中
        Position = new PixelPoint(
            bounds.X + Math.Max(0, (bounds.Width - width) / 2),
            bounds.Y + Math.Max(0, bounds.Height - height - (int)(72 * scale)));
    }

    private void UpdateElapsed()
    {
        if (_session is null)
            return;

        ElapsedText.Text = _session.Elapsed.ToString(@"hh\:mm\:ss");
    }

    private void OnStateChanged(RecordingState state, string? error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (state)
            {
                case RecordingState.Recording:
                    PauseGlyph.Text = "\uE769"; // 暂停
                    PauseButton.IsEnabled = true;
                    RecordButton.IsEnabled = true;
                    break;

                case RecordingState.Paused:
                    PauseGlyph.Text = "\uE768"; // 继续
                    PauseButton.IsEnabled = true;
                    break;

                case RecordingState.Stopping:
                    PauseButton.IsEnabled = false;
                    RecordButton.IsEnabled = false;
                    break;

                case RecordingState.Completed:
                    FinishWith(_session?.OutputPath);
                    break;

                case RecordingState.Faulted:
                    FinishWith(null);
                    break;
            }
        });
    }

    private void FinishWith(string? path)
    {
        if (_closed)
            return;

        _closed = true;
        var copy = _copyOnFinish;
        _copyOnFinish = false;
        var copied = path is not null && copy && RecordingClipboard.CopyFile(path);

        Finished?.Invoke(path, copied);
        Close();
    }

    private async void OnStopClick(object? sender, RoutedEventArgs e) =>
        await StopAndFinishAsync();

    /// <summary>停止 → 保存 → 把录屏文件放进剪贴板（对齐 Snow-shot 的 Copy recording）。</summary>
    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_session is null || _closed)
            return;

        _copyOnFinish = true;
        _ = StopAndFinishAsync();
    }

    /// <summary>外部（热键/托盘）请求停止并保存。</summary>
    public void RequestStop()
    {
        if (_session is null || _closed)
            return;

        _ = StopAndFinishAsync();
    }

    private async Task StopAndFinishAsync()
    {
        if (_session is null || _closed)
            return;

        RecordButton.IsEnabled = false;
        PauseButton.IsEnabled = false;

        var path = await _session.StopAsync();
        FinishWith(path);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        OnStopClick(sender, e);

    private void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
            return;

        if (_session.State == RecordingState.Recording)
            _session.Pause();
        else if (_session.State == RecordingState.Paused)
            _session.Resume();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _session?.Abandon();
        FinishWith(null);
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_session?.OutputPath);

        if (directory is null || !Directory.Exists(directory))
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", directory)
        {
            UseShellExecute = true
        });
    }

    private void OnDragHandle(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
