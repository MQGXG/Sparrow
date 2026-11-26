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
    private const uint WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private RecordingSession? _recording;
    private RecordingRegionOverlay? _regionOverlay;
    private DispatcherTimer? _recordTimer;
    private Win32Properties.CustomWndProcHookCallback? _hitTestHook;
    private bool _copyOnStop;

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

        var session = await RecordingSessionStarter(SelectionRect);

        if (session is null)
            return;

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
        {
            RecordElapsed.Text = _recording?.Elapsed.ToString(@"hh\:mm\:ss") ?? "00:00:00";

            // 工具条被拖动、浮层开合后区域会变：定时兜底刷新，保证点击穿透跟着走。
            UpdateRecordingWindowRegion();
        };
        _recordTimer.Start();

        await Task.Yield();

        // 布局稳定后再裁剪窗口：只有工具条（和标注中的选区）接收鼠标。
        UpdateRecordingWindowRegion();

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

        // 恢复二级菜单，窗口如果因为失败没有关闭也能继续操作。
        RecordButton.Flyout = _recordFlyout;
        _recordFlyout = null;
        ToolTip.SetTip(RecordButton, "录制（R）");

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

        RestoreWindowRegion();
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

        RecordButton.Flyout = _recordFlyout;
        _recordFlyout = null;

        _recording.StateChanged -= OnRecordingStateChanged;
        _recording.Abandon();
        _recording = null;
        _recordTimer?.Stop();

        _regionOverlay?.Close();
        _regionOverlay = null;

        RestoreWindowRegion();
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

    // ==== 录制中的窗口区域裁剪 ====
    // WM_NCHITTEST 返回 HTTRANSPARENT 只在同一线程的窗口之间转发，
    // 跨进程时下层应用拿不到点击（表现为「页面点不动」）。
    // 所以这里把窗口区域裁剪成「工具条 + 选中标注工具时的选区」：
    // 区域外的点击系统根本不会派发给截图窗口，录制中电脑可以正常操作。

    private const int RgnOr = 2;

    /// <summary>上一次设置的窗口区域（物理像素），相同就跳过，避免反复重绘。</summary>
    private PixelRect[]? _recordingRegionRects;

    /// <summary>按工具条/浮层/工具状态重设窗口区域；结束录制后调用 <see cref="RestoreWindowRegion"/>。</summary>
    private void UpdateRecordingWindowRegion()
    {
        if (_recording is null || _captureHandle == IntPtr.Zero)
            return;

        var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var rects = new List<PixelRect>();

        // 工具条有圆角和阴影，留一点余量，别把边缘裁掉。
        const double padding = 10;

        void AddRect(Rect rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            var left = (int)Math.Floor((rect.X - padding) * scale);
            var top = (int)Math.Floor((rect.Y - padding) * scale);
            var right = (int)Math.Ceiling((rect.Right + padding) * scale);
            var bottom = (int)Math.Ceiling((rect.Bottom + padding) * scale);

            rects.Add(new PixelRect(left, top, right - left, bottom - top));
        }

        void AddControl(Control control)
        {
            if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                return;

            // 直接读 Canvas 坐标：窗口被 SetWindowRgn 裁剪后，
            // TranslatePoint 会跟着重排后的布局变，形成「裁剪 → 重排 → 再裁剪」的抖动。
            if (control.Parent is Canvas)
            {
                var left = Canvas.GetLeft(control);
                var top = Canvas.GetTop(control);

                AddRect(new Rect(
                    double.IsNaN(left) ? 0 : left,
                    double.IsNaN(top) ? 0 : top,
                    control.Bounds.Width,
                    control.Bounds.Height));
                return;
            }

            if (control.TranslatePoint(new Point(0, 0), this) is not { } origin)
                return;

            AddRect(new Rect(origin, control.Bounds.Size));
        }

        AddControl(ActionBar);

        // 选了标注工具：选区内部要能画；没选工具时选区内也穿透，页面才点得动。
        if (_tool != AnnotationTool.None)
            AddRect(_selection);

        if (ToolGroupPopover.IsVisible)
            AddControl(ToolGroupPopover);

        if (CustomColorPanel.IsVisible)
            AddControl(CustomColorPanel);

        if (rects.Count == 0)
            return;

        if (_recordingRegionRects is { } previous
            && previous.Length == rects.Count
            && previous.SequenceEqual(rects))
            return;

        _recordingRegionRects = [.. rects];

        App.Log.Info(
            $"[record] 窗口区域：scale={scale:0.##} pos={Position} " +
            $"origin={_origin} rects={string.Join(" ", rects.Select(r => $"{r.X},{r.Y},{r.Width}x{r.Height}"))}");

        var region = CreateRectRgn(0, 0, 0, 0);

        foreach (var rect in rects)
        {
            var piece = CreateRectRgn(rect.X, rect.Y, rect.Right, rect.Bottom);
            CombineRgn(region, region, piece, RgnOr);
            DeleteObject(piece);
        }

        // SetWindowRgn 成功后区域归系统所有，不能删；失败才自己释放。
        if (SetWindowRgn(_captureHandle, region, true) == 0)
            DeleteObject(region);
    }

    /// <summary>结束录制：恢复整窗区域，否则之后截图窗口只有工具条能画。</summary>
    private void RestoreWindowRegion()
    {
        _recordingRegionRects = null;

        if (_captureHandle == IntPtr.Zero)
            return;

        SetWindowRgn(_captureHandle, IntPtr.Zero, true);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
