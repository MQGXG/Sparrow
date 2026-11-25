// CaptureWindow 分部类：录制控制并入主工具条，选区层由穿透红框负责。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sparrow.App.Overlays;
using Sparrow.App.Services.Recording;

namespace Sparrow.App.Views;

public partial class CaptureWindow
{
    private const uint WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private RecordingSession? _recording;
    private RecordingRegionOverlay? _regionOverlay;
    private DispatcherTimer? _recordTimer;
    private Win32Properties.CustomWndProcHookCallback? _hitTestHook;
    private bool _copyOnStop;

    /// <summary>由宿主注入：用选区创建录制会话；返回 null 表示启动失败。</summary>
    internal Func<PixelRect, Task<RecordingSession?>>? RecordingSessionStarter { get; set; }

    /// <summary>录制结束：成功给文件路径，取消/失败给 null；第二个参数是失败原因，第三个是是否已复制到剪贴板。</summary>
    public event Action<string?, string?, bool>? RecordingFinished;

    /// <summary>开发/自动化：直接以指定区域开始录制（不经过鼠标拖选）。</summary>
    internal void StartRecordingNow(PixelRect region)
    {
        var scale = _scaling <= 0 ? 1.0 : _scaling;

        _selection = new Rect(
            (region.X - _origin.X) / scale,
            (region.Y - _origin.Y) / scale,
            region.Width / scale,
            region.Height / scale);

        _hasSelection = true;
        UpdateSelectionVisuals();

        // 自动入口没有经过拖拽结束，工具条不会自己出现；这里显式打开并定位。
        ShowActionBar(_selection);

        // 窗口刚打开时工具条还没布局，这里等一次 Loaded 再走录制流程。
        Dispatcher.UIThread.Post(
            () => _ = ActAsync(CaptureAction.Record),
            DispatcherPriority.Loaded);
    }

    /// <summary>区域录制中：外部（热键）请求停止并保存。</summary>
    internal void RequestStopRecording() => _ = StopRecordingAsync();

    /// <summary>
    /// 开始录制：选区层隐藏，录制控制并入主工具条；
    /// 选区红框交给穿透覆盖层，标注画在窗口上，选区外点击穿透，电脑可正常操作。
    /// </summary>
    private async Task StartRecordingAsync()
    {
        if (_recording is not null || RecordingSessionStarter is null)
            return;

        var session = await RecordingSessionStarter(SelectionRect);

        if (session is null)
            return;

        _recording = session;
        session.StateChanged += OnRecordingStateChanged;

        // 进入录制时回到指针工具：不选工具就保持点击穿透。
        _tool = AnnotationTool.None;
        UpdateToolButtons();

        // 开始/停止共用 RecordButton：红点变方块；录制控制条作为第二行出现。
        RecordDot.IsVisible = false;
        RecordStop.IsVisible = true;
        RecordingRow.IsVisible = true;

        // 多了一行后重新按「上/下」排一次行序，保证录制条始终在离选区最远的外侧。
        _barOrderApplied = false;
        ApplyBarOrder(false);

        RecordMicrophoneGlyph.Opacity = session.HasMicrophone ? 1 : 0.3;
        RecordSpeakerGlyph.Opacity = session.HasSystemAudio ? 1 : 0.3;

        // 与 Snow-shot 一致：录制中 OCR / 翻译不可用。
        RecognizeButton.IsVisible = false;
        TranslateActionButton.IsVisible = false;
        MoreRecognitionButton.IsVisible = false;

        // 控制组让工具条变宽：录制期间工具条必须在选区之外，否则会被录进视频。
        PlaceToolbarOutsideSelection();

        HideSelectionLayer();

        // 标注画布保留：录制中画笔/矩形/箭头等标注实时显示，并会被录进视频。
        NoteLayer.IsVisible = true;

        // 安装 WM_NCHITTEST 钩子：选区外返回 HTTRANSPARENT，点击落到下层窗口。
        InstallHitTestHook();

        _regionOverlay = new RecordingRegionOverlay(SelectionRect);
        _regionOverlay.Show();

        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _recordTimer.Tick += (_, _) =>
            RecordElapsed.Text = _recording?.Elapsed.ToString(@"hh\:mm\:ss") ?? "00:00:00";
        _recordTimer.Start();

        await Task.Yield();

        var toolbar = ToolbarRect(ActionBar.DesiredSize);
        App.Log.Info(
            $"[record] 工具条位置：{toolbar.X},{toolbar.Y} " +
            $"{toolbar.Width}x{toolbar.Height}");
    }

    private async Task StopRecordingAsync()
    {
        if (_recording is null)
            return;

        RecordButton.IsEnabled = false;
        PauseRecordButton.IsEnabled = false;

        var path = await _recording.StopAsync();
        var error = path is null ? _recording.LastError : null;
        var copy = _copyOnStop;
        _copyOnStop = false;
        var copied = path is not null && copy && RecordingClipboard.CopyFile(path);

        _recording.StateChanged -= OnRecordingStateChanged;
        _recording.Dispose();
        _recording = null;
        _recordTimer?.Stop();

        _regionOverlay?.Close();
        _regionOverlay = null;

        Close();
        RecordingFinished?.Invoke(path, error, copied);
    }

    /// <summary>
    /// 复制录屏：停止 → 保存 → 把文件放进剪贴板（对齐 Snow-shot 的 Copy recording）。
    /// </summary>
    private void OnCopyRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (_recording is null)
            return;

        _copyOnStop = true;
        CopyRecordingButton.IsEnabled = false;
        _ = StopRecordingAsync();
    }

    private void OnRecordingStopClick(object? sender, RoutedEventArgs e) =>
        _ = StopRecordingAsync();

    private void OnCancelRecordingClick(object? sender, RoutedEventArgs e)
    {        if (_recording is null)
            return;

        _recording.StateChanged -= OnRecordingStateChanged;
        _recording.Abandon();
        _recording = null;
        _recordTimer?.Stop();

        _regionOverlay?.Close();
        _regionOverlay = null;

        Close();
        RecordingFinished?.Invoke(null, null, false);
    }

    private void OnPauseRecordingClick(object? sender, RoutedEventArgs e)
    {
        if (_recording is null)
            return;

        if (_recording.State == RecordingState.Recording)
            _recording.Pause();
        else if (_recording.State == RecordingState.Paused)
            _recording.Resume();

        PauseRecordGlyph.Text = _recording.State == RecordingState.Paused
            ? "\uE768"
            : "\uE769";
    }

    private void OnOpenRecordFolderClick(object? sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_recording?.OutputPath);

        if (directory is null || !Directory.Exists(directory))
            return;

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", directory)
            {
                UseShellExecute = true
            });
    }

    private void OnRecordingStateChanged(RecordingState state, string? error)
    {
        if (error is not null)
            App.Log.Error($"[record] {error}");
    }

    /// <summary>录制中隐藏所有选区/遮罩层，只保留主工具条和标注层。</summary>
    private void HideSelectionLayer()
    {
        FrozenFrame.IsVisible = false;
        DimLayer.IsVisible = false;
        CrosshairLayer.IsVisible = false;
        CandidateBox.IsVisible = false;
        CandidateBadge.IsVisible = false;
        Selection.IsVisible = false;
        SelectionHalo.IsVisible = false;
        HandleLayer.IsVisible = false;
        GuideLayer.IsVisible = false;
        NoteHandleLayer.IsVisible = false;
        Magnifier.IsVisible = false;
        ColorProbe.IsVisible = false;
        SizeBadge.IsVisible = false;
        HintBar.IsVisible = false;
        AngleHint.IsVisible = false;
        OcrLayer.IsVisible = false;
        TransLayer.IsVisible = false;
    }

    /// <summary>
    /// 录制控制条不能出现在视频里：先按正常规则贴到选区边缘；
    /// 如果还压在选区上（手动拖进去了 / 选区太大），强制挪到选区外。
    /// </summary>
    private void PlaceToolbarOutsideSelection()
    {
        MeasureActionBar();

        var size = ActionBar.DesiredSize;

        if (size.Width <= 0 || size.Height <= 0)
            return;

        if (!ToolbarRect(size).Intersects(_selection))
            return;

        // 录制期间临时忽略手动位置，不改用户记住的工具条习惯。
        _toolbarManual = false;
        PositionActionBar(_selection);

        if (!ToolbarRect(size).Intersects(_selection))
            return;

        // 选区几乎占满屏幕，工具条无处可放；这种情况只能让它进视频（标注也在同一窗口）。
        App.Log.Warn("[record] 工具条与选区重叠且无空位");
    }

    private Rect ToolbarRect(Size size)
    {
        var left = Canvas.GetLeft(ActionBar);
        var top = Canvas.GetTop(ActionBar);

        return new Rect(
            double.IsNaN(left) ? 0 : left,
            double.IsNaN(top) ? 0 : top,
            size.Width,
            size.Height);
    }

    // ==== 录制中的点击穿透 ====

    /// <summary>
    /// 安装窗口过程钩子：录制中只有工具条、以及选中标注工具后的选区内部接收点击，
    /// 其余区域返回 HTTRANSPARENT，鼠标落到下层应用，电脑可以正常操作。
    /// </summary>
    private void InstallHitTestHook()
    {
        if (_hitTestHook is not null)
            return;

        _hitTestHook = OnHitTest;
        Win32Properties.AddWndProcHookCallback(this, _hitTestHook);
    }

    private void RemoveHitTestHook()
    {
        if (_hitTestHook is null)
            return;

        Win32Properties.RemoveWndProcHookCallback(this, _hitTestHook);
        _hitTestHook = null;
    }

    private IntPtr OnHitTest(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != WmNcHitTest || _recording is null)
            return IntPtr.Zero;

        var value = lParam.ToInt64();
        var screenX = (short)(value & 0xFFFF);
        var screenY = (short)((value >> 16) & 0xFFFF);

        // 无边框窗口的客户区原点就是窗口位置；换算成窗口内 DIP 坐标。
        var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var point = new Point(
            (screenX - Position.X) / scale,
            (screenY - Position.Y) / scale);

        if (IsRecordingInteractiveAt(point))
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(HtTransparent);
    }

    private bool IsRecordingInteractiveAt(Point point)
    {
        // 工具条本身（含属性行、工具组选项条）要能点。
        if (IsPointInActionBar(point))
            return true;

        if (ToolGroupPopover.IsVisible && IsPointInControl(ToolGroupPopover, point))
            return true;

        if (CustomColorPanel.IsVisible && IsPointInCustomColorPanel(point))
            return true;

        // 选了标注工具：选区内部交给画布，选区外仍然穿透。
        return _tool != AnnotationTool.None && _selection.Contains(point);
    }

    /// <summary>录制中是否允许在这一点开始/继续标注。</summary>
    private bool CanAnnotateDuringRecording(Point point) =>
        _recording is null
        || (_tool != AnnotationTool.None && _selection.Contains(point));
}
