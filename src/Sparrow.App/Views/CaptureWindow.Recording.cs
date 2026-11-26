// CaptureWindow 分部类：录制控制并入主工具条，选区层由穿透红框负责。
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sparrow.App.Overlays;
using Sparrow.App.Services.Recording;

namespace Sparrow.App.Views;

public partial class CaptureWindow
{
    private const int RgnOr = 2;

    private RecordingSession? _recording;
    private RecordingRegionOverlay? _regionOverlay;
    private RecordingToolbarWindow? _toolbarWindow;
    private DispatcherTimer? _recordTimer;
    private List<PixelRect> _recordingRegion = [];
    private IntPtr _recordingRegionHandle;
    private bool _copyOnStop;
    private bool _stopping;
    private bool _startingRecording;

    /// <summary>由宿主注入：用选区创建录制会话；返回 null 表示启动失败。</summary>
    internal Func<PixelRect, Task<RecordingSession?>>? RecordingSessionStarter { get; set; }

    /// <summary>由宿主注入：打开完整录制设置面板；确认后宿主仍用当前选区开始。</summary>
    internal Action<PixelRect>? RecordingSetupRequested { get; set; }

    /// <summary>设置面板确认后，用当前选区开始录制（走截图窗口里的录制条）。</summary>
    internal void StartRecordingFromSelection() => _ = StartRecordingAsync();

    /// <summary>录制开始时按钮变成「停止」，先摘掉 Flyout，避免点停止又弹出菜单。</summary>
    private FlyoutBase? _recordFlyout;

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

        var toolbarWasVisible = ActionBar.IsVisible;

        // 先隐藏选区/候选/读数，再启动 WGC；否则录制的第一段会把截图遮罩录进去。
        _startingRecording = true;
        HideSelectionLayer();
        ActionBar.IsVisible = false;

        RecordingSession? session;

        try
        {
            session = await RecordingSessionStarter(SelectionRect);
        }
        catch
        {
            _startingRecording = false;
            ActionBar.IsVisible = toolbarWasVisible;
            UpdateSelectionVisuals();
            throw;
        }

        if (session is null)
        {
            _startingRecording = false;
            ActionBar.IsVisible = toolbarWasVisible;
            UpdateSelectionVisuals();
            return;
        }

        _recording = session;
        session.StateChanged += OnRecordingStateChanged;

        // 录制中按钮就是「停止」：先摘掉二级菜单，点一下直接停。
        _recordFlyout = RecordButton.Flyout;
        RecordButton.Flyout = null;
        ToolTip.SetTip(RecordButton, "停止录制（Ctrl+Alt+Shift+R）");

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

        // 现在才恢复工具条：先完成布局定位，再搬到排除捕获的独立窗口。
        ActionBar.IsVisible = true;
        PlaceToolbarOutsideSelection();

        // UpdateToolButtons 等路径可能重新显示过选区/读数，再压一次。
        HideSelectionLayer();

        // 标注画布保留：录制中画笔/矩形/箭头等标注实时显示，并会被录进视频。
        NoteLayer.IsVisible = true;

        // 摄像头预览先显示，工具条窗口后显示，保证工具条在预览之上。
        ShowCameraPreview();
        MoveToolbarToExcludedWindow();

        // 窗口区域裁剪：选区外不属于窗口，鼠标直接落到下层应用（跨进程有效）。
        // 原来的 WM_NCHITTEST→HTTRANSPARENT 只对同线程窗口有效，会吞掉整个桌面的点击。
        LayoutUpdated += OnRecordingLayoutUpdated;

        _regionOverlay = new RecordingRegionOverlay(SelectionRect);
        _regionOverlay.Show();

        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _recordTimer.Tick += (_, _) =>
        {
            RecordElapsed.Text = _recording?.Elapsed.ToString(@"hh\:mm\:ss") ?? "00:00:00";
            UpdateRecordingInputRegion();
        };
        _recordTimer.Start();

        UpdateRecordingInputRegion();
        _startingRecording = false;

        await Task.Yield();

        // 布局稳定后再刷新一次区域：只有工具条（和标注中的选区）接收鼠标。
        UpdateRecordingInputRegion();

        var toolbar = ToolbarRect(ActionBar.DesiredSize);
        App.Log.Info(
            $"[record] 工具条位置：{toolbar.X},{toolbar.Y} " +
            $"{toolbar.Width}x{toolbar.Height}");
    }

    private async Task StopRecordingAsync()
    {
        if (_recording is null || _stopping)
            return;

        _stopping = true;

        RecordButton.IsEnabled = false;
        PauseRecordButton.IsEnabled = false;

        // 恢复二级菜单，窗口如果因为失败没有关闭也能继续操作。
        RecordButton.Flyout = _recordFlyout;
        _recordFlyout = null;
        ToolTip.SetTip(RecordButton, "录制（R）");

        var path = await _recording.StopAsync();
        var error = path is null ? _recording.LastError : null;
        var copy = _copyOnStop;
        _copyOnStop = false;
        var copied = path is not null && copy && RecordingClipboard.CopyFile(path);

        CloseRecordingOverlays();

        _recording.StateChanged -= OnRecordingStateChanged;
        _recording.Dispose();
        _recording = null;
        _recordTimer?.Stop();
        EndRecordingInputRegion();

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

        CloseRecordingOverlays();

        RecordButton.Flyout = _recordFlyout;
        _recordFlyout = null;

        _recording.StateChanged -= OnRecordingStateChanged;
        _recording.Abandon();
        _recording = null;
        _recordTimer?.Stop();
        EndRecordingInputRegion();

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

        // 暂停/继续用两个图标切换，和独立录制条保持一致。
        var paused = _recording.State == RecordingState.Paused;
        PauseRecordGlyph.IsVisible = !paused;
        ResumeRecordGlyph.IsVisible = paused;
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

        // 到时长上限/失败时自动收尾，避免录制条一直停在那里。
        if (state is RecordingState.Completed or RecordingState.Faulted)
            Dispatcher.UIThread.Post(() => _ = StopRecordingAsync());
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

        // 选区几乎占满屏幕，工具条没有空位可放。
        // 工具条在独立的排除窗口里，压在选区上也不会进视频，只是会挡住一点画面。
        App.Log.Info("[record] 工具条与选区重叠（工具条窗口已排除捕获）");
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

    /// <summary>工具条浮层定位的坐标基准：录制中工具条已经搬到独立窗口。</summary>
    private Visual ToolbarCoordinateRoot => _toolbarWindow?.Canvas ?? (Visual)this;

    /// <summary>录制中把预览的布局目标切到当前会话；窗口本身已在创建会话时显示。</summary>
    private void ShowCameraPreview()
    {
        if (_recording?.Camera is not null)
            CameraPreviewHost.Attach(_recording);
    }

    /// <summary>
    /// 把工具条及其浮层搬到独立的、已排除捕获的窗口。
    /// 这样即使工具条压在选区上，也只会出现在用户屏幕上，不会进视频。
    /// </summary>
    private void MoveToolbarToExcludedWindow()
    {
        if (_toolbarWindow is not null)
            return;

        var controls = new Control[] { ActionBar, ToolGroupPopover, RecognitionPopover, CustomColorPanel };

        foreach (var control in controls)
            Root.Children.Remove(control);

        _toolbarWindow = new RecordingToolbarWindow(
            controls,
            _origin,
            _scaling,
            Width,
            Height);

        _toolbarWindow.Show();
    }

    private void CloseRecordingOverlays()
    {
        CameraPreviewHost.Close();

        if (_toolbarWindow is not null)
        {
            _toolbarWindow.Close();
            _toolbarWindow = null;
        }
    }

    private void OnRecordingLayoutUpdated(object? sender, EventArgs e) =>
        UpdateRecordingInputRegion();

    /// <summary>
    /// 用窗口区域裁剪做穿透：区域外的像素不属于本窗口，鼠标会直接落到桌面其他程序。
    /// WM_NCHITTEST→HTTRANSPARENT 只对同线程窗口有效，跨进程点击会被整块全屏窗口吞掉。
    ///
    /// 保留可交互的部分：工具条、可见的弹出面板，以及需要绘制/显示标注的选区。
    /// 其余桌面区域不参与命中测试，录制时电脑可以正常操作。
    /// </summary>
    private void UpdateRecordingInputRegion()
    {
        if (_recording is null)
            return;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        var scale = _scaling > 0 ? _scaling : 1.0;
        var rects = new List<PixelRect>();

        void Add(Control control)
        {
            if (!control.IsVisible)
                return;

            if (control.TranslatePoint(new Point(0, 0), this) is not { } origin)
                return;

            var size = control.Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            var rect = ToWindowPixels(new Rect(origin, size), scale);
            if (rect.Width > 0 && rect.Height > 0)
                rects.Add(rect);
        }

        // 工具条通常已经在独立的排除窗口里；只有迁移失败/未迁移时才留在本窗口。
        if (_toolbarWindow is null)
        {
            Add(ActionBar);
            Add(ToolGroupPopover);
            Add(RecognitionPopover);
            Add(CustomColorPanel);
        }

        // 选了标注工具时要能画；已有标注时也必须保留选区，
        // 否则窗口区域裁剪会把已经画好的标注一起裁掉（屏幕上和视频里都会消失）。
        if (_hasSelection && (_tool != AnnotationTool.None || _notes.Items.Count > 0))
            rects.Add(ToWindowPixels(_selection, scale));

        // 布局每帧都可能触发：区域没变就不要反复 SetWindowRgn。
        if (hwnd == _recordingRegionHandle
            && rects.Count == _recordingRegion.Count
            && rects.SequenceEqual(_recordingRegion))
            return;

        var region = CreateRectRgn(0, 0, 0, 0);

        foreach (var rect in rects)
        {
            var piece = CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
            CombineRgn(region, region, piece, RgnOr);
            DeleteObject(piece);
        }

        // 成功后 region 归系统所有，不能再 DeleteObject。
        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
            _recordingRegion = [];
            _recordingRegionHandle = IntPtr.Zero;
            return;
        }

        _recordingRegion = rects;
        _recordingRegionHandle = hwnd;
    }

    /// <summary>DIP → 窗口客户区物理像素，并夹到窗口范围内。</summary>
    private PixelRect ToWindowPixels(Rect rect, double scale)
    {
        var maxRight = _virtualWidth > 0 ? _virtualWidth : (int)Math.Ceiling(rect.Right * scale);
        var maxBottom = _virtualHeight > 0 ? _virtualHeight : (int)Math.Ceiling(rect.Bottom * scale);

        var left = Math.Clamp((int)Math.Floor(rect.X * scale), 0, maxRight);
        var top = Math.Clamp((int)Math.Floor(rect.Y * scale), 0, maxBottom);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right * scale), left, maxRight);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom * scale), top, maxBottom);

        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>停止/取消录制：解除布局监听并恢复完整窗口区域。</summary>
    private void EndRecordingInputRegion()
    {
        LayoutUpdated -= OnRecordingLayoutUpdated;
        _recordingRegion = [];
        _recordingRegionHandle = IntPtr.Zero;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;

        // IntPtr.Zero = 取消裁剪；系统会释放上一次的 region。
        SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    /// <summary>录制中是否允许在这一点开始/继续标注。</summary>
    private bool CanAnnotateDuringRecording(Point point) =>
        _recording is null
        || (_tool != AnnotationTool.None && _selection.Contains(point));

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

