// App 分部类：屏幕录制（宿主能力，不是插件）。
using System.Diagnostics;
using Avalonia;
using Sparrow.App.Overlays;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;
using Sparrow.App.Views;

namespace Sparrow.App;

public partial class App
{
    /// <summary>当前录制控制条；非 null 表示正在录制，防止重复启动。</summary>
    private RecordingBarWindow? _recordingBar;

    /// <summary>区域模式：设置面板选完后暂存参数，等截图窗口返回选区再启动。</summary>
    private RecordingSetup? _pendingRecordingSetup;

    /// <summary>当前区域录制的截图窗口（控制条在它里面）。</summary>
    private CaptureWindow? _activeCaptureWindow;

    /// <summary>热键入口：未录制时打开设置面板，录制中则停止并保存（一次热键闭环）。</summary>
    internal void ToggleScreenRecording()
    {
        if (_activeCaptureWindow is not null)
        {
            _activeCaptureWindow.RequestStopRecording();
            return;
        }

        if (_recordingBar is not null)
        {
            _recordingBar.RequestStop();
            return;
        }

        StartScreenRecording();
    }

    /// <summary>打开录制设置面板（热键/托盘/--setup 共用）。</summary>
    internal void OpenRecordingSetup()
    {
        if (_recordingBar is not null)
        {
            _toasts?.Show("已经在录制中。");
            return;
        }

        var panel = new RecordingSetupWindow(_appSettings.RecordEncoder);

        panel.Finished += setup =>
        {
            if (setup is null)
                return;

            PersistRecordingEncoder(setup.Encoder);

            if (setup.Mode == RecordingMode.Region)
            {
                // 选区参数要穿过截图窗口：等用户在选区工具条上点红点再启动。
                _pendingRecordingSetup = setup;
                CaptureAndRecognize();
                return;
            }

            if (setup.Mode == RecordingMode.Window)
            {
                _ = PickWindowAndRecordAsync(setup);
                return;
            }

            _ = StartRecordingCoreAsync(WgcCapture.GetPrimaryMonitorBounds(), setup);
        };

        panel.Show();
    }

    /// <summary>把用户选的编码器写回 settings.json；下次打开面板继续用。</summary>
    private void PersistRecordingEncoder(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder)
            || string.Equals(_appSettings.RecordEncoder, encoder, StringComparison.OrdinalIgnoreCase))
            return;

        _appSettings = _appSettings with { RecordEncoder = encoder };
        _settingsStore?.Save(_appSettings);
    }

    /// <summary>
    /// 录制入口。
    /// <paramref name="region"/> 非空时直接录该区域（截图窗口的「录制」按钮）；
    /// 为空时先弹设置面板（区域 / 窗口 / 全屏、质量、帧率、鼠标、音频、水印）。
    /// </summary>
    internal void StartScreenRecording(PixelRect? region = null)
    {
        if (_recordingBar is not null)
        {
            _toasts?.Show("已经在录制中。");
            return;
        }

        if (region is not null)
        {
            var setup = _pendingRecordingSetup ?? RecordingSetup.Default;
            _pendingRecordingSetup = null;
            _ = StartRecordingCoreAsync(region.Value, setup);
            return;
        }

        OpenRecordingSetup();
    }

    /// <summary>窗口模式：先拾取窗口，再按窗口内容录制。</summary>
    private async Task PickWindowAndRecordAsync(RecordingSetup setup)
    {
        if (_windowOverlay is null)
        {
            _toasts?.Show("窗口拾取不可用。");
            return;
        }

        var window = await _windowOverlay.PickWindowAsync(
            "选择要录制的窗口",
            CancellationToken.None);

        if (window == 0)
        {
            _toasts?.Show("已取消窗口录制。");
            return;
        }

        _ = StartRecordingCoreAsync(window, setup);
    }

    private Task StartRecordingCoreAsync(PixelRect region, RecordingSetup setup) =>
        StartRecordingCoreAsync(region, IntPtr.Zero, setup);

    private Task StartRecordingCoreAsync(IntPtr window, RecordingSetup setup) =>
        StartRecordingCoreAsync(default, window, setup);

    /// <summary>真正的录制启动：倒计时 → WGC 抓帧 → ffmpeg 编码 → 控制条。</summary>
    private async Task StartRecordingCoreAsync(
        PixelRect region,
        IntPtr window,
        RecordingSetup setup)
    {
        if (_recordingBar is not null)
        {
            _toasts?.Show("已经在录制中。");
            return;
        }

        var session = await CreateRecordingSessionAsync(region, window, setup);

        if (session is null)
            return;

        var bar = new RecordingBarWindow(session, region);
        _recordingBar = bar;

        bar.Finished += (path, copied) =>
        {
            _recordingBar = null;
            session.Dispose();

            if (path is null)
            {
                _toasts?.Show("已取消录制。");
                return;
            }

            var file = new FileInfo(path);
            var sizeMb = file.Exists ? file.Length / 1024.0 / 1024.0 : 0;

            _toasts?.Show(
                copied
                    ? $"录制完成并已复制到剪贴板：{file.Name}（{sizeMb:F1} MB）"
                    : $"录制完成：{file.Name}（{sizeMb:F1} MB）",
                TimeSpan.FromSeconds(5));

            if (copied)
                return;

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        };

        bar.Show();

        _toasts?.Show("开始录制 · Ctrl+Alt+Shift+R 可再次停止", TimeSpan.FromSeconds(3));
        Log.Info(
            $"[record] 开始录制：{session.OutputPath}" +
            $"（{session.FramesPerSecond}fps，CRF {setup.Crf}，编码器 {session.EncoderName}）");
    }

    /// <summary>创建录制会话（倒计时 + 路径 + 选项）；不显示任何控制条。</summary>
    private async Task<RecordingSession?> CreateRecordingSessionAsync(
        PixelRect region,
        IntPtr window,
        RecordingSetup setup)
    {
        // 首次录制（或刚克隆仓库）没有 ffmpeg：先下载，避免“点开始才发现编码器缺失”。
        if (FfmpegLocator.Find() is null)
        {
            var setupWindow = new FfmpegSetupWindow();
            setupWindow.Show();
            var ffmpeg = await setupWindow.Result;

            if (ffmpeg is null)
            {
                _toasts?.Show(
                    string.IsNullOrWhiteSpace(setupWindow.Error)
                        ? "已取消下载录制编码器。"
                        : $"FFmpeg 下载失败：{setupWindow.Error}。可手动放到 tools/ffmpeg/。",
                    TimeSpan.FromSeconds(8));
                return null;
            }
        }

        if (setup.CountdownSeconds > 0)
        {
            var countdown = new CountdownWindow(setup.CountdownSeconds);
            countdown.Show();

            if (!await countdown.Result)
            {
                _toasts?.Show("已取消录制。");
                return null;
            }
        }

        var directory = string.IsNullOrWhiteSpace(setup.OutputDirectory)
            ? SparrowPaths.Recordings
            : setup.OutputDirectory;

        var baseName = string.IsNullOrWhiteSpace(setup.BaseName) ? "Sparrow" : setup.BaseName;
        var extension = setup.Format switch
        {
            "gif" => ".gif",
            "apng" => ".apng",
            "mkv" => ".mkv",
            "mov" => ".mov",
            _ => ".mp4"
        };

        var outputPath = Path.Combine(
            directory,
            $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");

        var options = new RecordingOptions
        {
            Region = region,
            WindowHandle = window,
            OutputPath = outputPath,
            FramesPerSecond = setup.FramesPerSecond,
            Crf = setup.Crf,
            Preset = setup.Preset,
            Encoder = setup.Encoder,
            CountdownSeconds = setup.CountdownSeconds,
            MaxDuration = setup.MaxDurationSeconds > 0
                ? TimeSpan.FromSeconds(setup.MaxDurationSeconds)
                : null,
            IncludeCursor = setup.IncludeCursor,
            SystemAudio = setup.SystemAudio,
            Microphone = setup.Microphone,
            Format = setup.Format,
            GifFps = setup.GifFps,
            GifWidth = setup.GifWidth,
            Watermark = setup.Watermark,
            GopSeconds = setup.GopSeconds,
            CursorRadius = setup.CursorRadius,
            CursorColor = setup.CursorColor,
            CursorOpacity = setup.CursorOpacity,
            CursorShadow = setup.CursorShadow
        };

        var session = RecordingSession.TryStart(options, out var error);

        if (session is null)
        {
            _toasts?.Show(error ?? "无法开始屏幕录制。", TimeSpan.FromSeconds(6));
            Log.Error($"[record] {error}");
            return null;
        }

        Log.Info(
            $"[record] 开始录制：{outputPath}" +
            $"（{session.FramesPerSecond}fps，CRF {setup.Crf}，编码器 {session.EncoderName}）");
        return session;
    }

    /// <summary>
    /// 开发期录制自检：录固定时长后输出结果并退出。
    /// 正常启动时 <see cref="RecordingProbe.TryStart"/> 返回 null，不产生任何副作用。
    /// </summary>
    private static async Task RunRecordingProbeAsync(
        RecordingSession session,
        int seconds,
        IAppLog log)
    {
        var stopwatch = Stopwatch.StartNew();

        session.StateChanged += (state, error) =>
        {
            if (error is null)
                log.Info($"[record-probe] 状态={state}");
            else
                log.Error($"[record-probe] 状态={state}：{error}");
        };

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);

            var path = await session.StopAsync().ConfigureAwait(false);
            var file = path is null ? null : new FileInfo(path);
            var sizeMb = file is { Exists: true } ? file.Length / 1024.0 / 1024.0 : 0;

            log.Info(
                $"[record-probe] 状态={session.State}，" +
                $"帧数={session.FramesWritten}，" +
                $"文件={path ?? "（无）"}，{sizeMb:F1} MB，" +
                $"耗时={stopwatch.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            log.Error("[record-probe] 录制失败", ex);
        }
        finally
        {
            // probe 是独立进程行为：不等待 Avalonia 消息循环与 WGC 收尾，直接结束。
            // 正常录制路径不会走到这里（由控制条/托盘控制）。
            log.Info("[record-probe] 退出");
            Environment.Exit(0);
        }
    }
}
