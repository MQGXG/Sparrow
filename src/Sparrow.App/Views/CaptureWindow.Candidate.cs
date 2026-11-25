// CaptureWindow 分部类：窗口与控件候选探针、命中路径与元素选择。
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
using SkiaSharp;

namespace Sparrow.App.Views;

public partial class CaptureWindow
{
    private void UpdateCandidateAt(Point point)
    {
        var physical = ToPhysical(point);

        // 探测/细化进行中，光标没真正移动就不要取消它；否则 1.5s 细化
        // 会被轻微的鼠标/焦点事件不断打断，浏览器永远等不到完整树。
        if (_probeTask is { IsCompleted: false }
            && _probePoint != default
            && !CursorMovedSince(_probePoint))
        {
            return;
        }

        RefreshCandidatesIfStale();

        // 不在“当前控件内就跳过探测”：Snow Shot 每次移动都会重新命中。
        // 同一行里从空白移到链接、从容器移到按钮，都可能出现更深的元素。
        SetWindowCandidate(WindowCandidates.Pick(_candidates, physical));
        ScheduleControlProbe();
    }

    private PixelPoint ToPhysical(Point point) => new(
        _origin.X + (int)Math.Round(point.X * _scaling),
        _origin.Y + (int)Math.Round(point.Y * _scaling));

    /// <summary>
    /// 截图期间会新开菜单 / 托盘溢出 / 弹窗；窗口列表每 500ms 刷新一次，
    /// 否则这些新 HWND 不在候选里，会去命中后面的应用。
    /// </summary>
    private void RefreshCandidatesIfStale()
    {
        var now = Environment.TickCount64;

        if (now - _candidatesAt < 500)
            return;

        _candidatesAt = now;

        if (_captureHandle == IntPtr.Zero)
            _captureHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        _candidates = WindowCandidates.Enumerate(_captureHandle);
    }

    private void SetWindowCandidate(WindowCandidate? candidate)
    {
        // 同一窗口（同一 HWND）：只更新窗口信息，保留当前命中路径；
        // 不要每次鼠标移动都把路径清空重来。
        if (candidate is not null
            && _currentCandidate is not null
            && candidate.Handle == _currentCandidate.Handle)
        {
            _currentCandidate = candidate;
            return;
        }

        _currentCandidate = candidate;
        _uiCandidate = null;
        _elementSelection.Reset();

        if (candidate is null)
        {
            HideCandidate();
            return;
        }

        SelectorLog($"window=0x{candidate.Handle.ToInt64():X} {candidate.Title} " +
                    $"{candidate.Bounds.X},{candidate.Bounds.Y} " +
                    $"{candidate.Bounds.Width}x{candidate.Bounds.Height}");

        SelectorTrace.Write(
            $"candidate hwnd=0x{candidate.Handle.ToInt64():X} class={candidate.ClassName} " +
            $"{candidate.Bounds.X},{candidate.Bounds.Y} " +
            $"{candidate.Bounds.Width}x{candidate.Bounds.Height} {candidate.Title}");

        ShowCandidateVisual(candidate.Bounds, candidate.Title, kind: null);
    }

    /// <summary>应用一次命中路径：剪掉重复层、按当前模式选层。</summary>
    private void ApplyHitPath(IReadOnlyList<UiElementCandidate> path)
    {
        if (_currentCandidate is null)
            return;

        if (!_elementSelection.ApplyPath(path, _currentCandidate.Bounds, MinimumElementSize))
            return;

        SelectorLog($"hit current={_elementSelection.Current?.ControlType} " +
                    $"{_elementSelection.Current?.Bounds} layers={_elementSelection.Path.Count}");

        SelectorTrace.Write(
            $"apply hit current={_elementSelection.Current?.ControlType} " +
            $"{_elementSelection.Current?.Bounds} layers={_elementSelection.Path.Count}");

        ShowCurrentHit();
    }

    /// <summary>细化路径优先按“旧路径扩展”处理；子窗口切换/树重建时允许同窗替换。</summary>
    private void ApplyRefinementPath(IReadOnlyList<UiElementCandidate> path)
    {
        if (_currentCandidate is null)
            return;

        if (_elementSelection.ApplyRefinement(
                path, _currentCandidate.Bounds, MinimumElementSize))
        {
            SelectorLog($"refine current={_elementSelection.Current?.ControlType} " +
                        $"{_elementSelection.Current?.Bounds} layers={_elementSelection.Path.Count}");
            ShowCurrentHit();
            return;
        }

        // UIA 顶层 Document → Chrome 渲染子窗口这类切换：旧层不会再出现，
        // 但仍是同一窗口、且新路径更深，按替换处理，否则会一直卡在文档层。
        if (_elementSelection.CanReplaceWith(
                path, _currentCandidate.Bounds, MinimumElementSize)
            && _elementSelection.ApplyPath(
                path, _currentCandidate.Bounds, MinimumElementSize))
        {
            SelectorLog($"replace current={_elementSelection.Current?.ControlType} " +
                        $"{_elementSelection.Current?.Bounds} layers={_elementSelection.Path.Count}");
            ShowCurrentHit();
        }
    }

    /// <summary>路径的物理像素最小尺寸。</summary>
    private int MinimumElementSize =>
        Math.Max(2, (int)Math.Ceiling(MinSelectionSize * _scaling));

    private void ShowCurrentHit()
    {
        var current = _elementSelection.Current;
        _uiCandidate = current;

        if (current is not null)
        {
            // 窗口层用 DWM 窗口矩形，UIA 的根矩形可能不含标题栏/阴影
            if (current.ControlType == "窗口" && _currentCandidate is not null)
            {
                ShowCandidateVisual(
                    _currentCandidate.Bounds, _currentCandidate.Title, kind: null);
                return;
            }

            ShowCandidateVisual(current.Bounds, current.Title, current.ControlType);
            return;
        }

        if (_currentCandidate is not null)
            ShowCandidateVisual(_currentCandidate.Bounds, _currentCandidate.Title, kind: null);
        else
            HideCandidate();
    }

    private void ShowCandidateVisual(PixelRect bounds, string title, string? kind)
    {
        var x = (bounds.X - _origin.X) / _scaling;
        var y = (bounds.Y - _origin.Y) / _scaling;
        var width = bounds.Width / _scaling;
        var height = bounds.Height / _scaling;

        Canvas.SetLeft(CandidateBox, x);
        Canvas.SetTop(CandidateBox, y);
        CandidateBox.Width = width;
        CandidateBox.Height = height;
        CandidateBox.IsVisible = true;

        // 候选窗口也挖开遮罩：悬停时整窗预览更接近参考工具的「智能选区」
        _candidateDip = new Rect(x, y, width, height);
        UpdateDim();

        var name = title.Length <= 32 ? title : title[..32] + "…";

        CandidateText.Text = kind is null
            ? $"{name}  {bounds.Width} × {bounds.Height}"
            : string.Equals(name, kind, StringComparison.Ordinal)
                ? $"{kind}  {bounds.Width} × {bounds.Height}"
                : $"{kind} · {name}  {bounds.Width} × {bounds.Height}";

        Canvas.SetLeft(CandidateBadge, Math.Max(0, x));
        Canvas.SetTop(CandidateBadge, y >= 34 ? y - 30 : y + 6);
        CandidateBadge.IsVisible = true;
    }

    private void HideCandidate()
    {
        _probeGeneration++;
        _currentCandidate = null;
        _uiCandidate = null;
        _elementSelection.Reset();
        HideCandidateVisuals();
    }

    private void HideCandidateVisuals()
    {
        CandidateBox.IsVisible = false;
        CandidateBadge.IsVisible = false;
        _candidateDip = default;
        UpdateDim();
    }

    /// <summary>Ctrl+Tab：在光标下重叠的窗口之间循环。</summary>
    private void CycleWindowCandidate()
    {
        if (_candidates.Count == 0)
            return;

        _probeGeneration++;
        _uiCandidate = null;
        _elementSelection.Reset();

        var previous = _currentCandidate;
        _currentCandidate = null;

        var next = WindowCandidates.Next(
            _candidates,
            WindowCandidates.GetCursorPosition(),
            previous);

        if (next is null)
        {
            HideCandidate();
            return;
        }

        _currentCandidate = next;
        ShowCandidateVisual(next.Bounds, next.Title, kind: null);
        ScheduleControlProbe();
    }

    /// <summary>Tab：整窗 ↔ 子元素模式切换。</summary>
    private void ToggleElementTarget()
    {
        _elementSelection.ToggleTarget();

        // 切回子元素但路径里只有窗口：重新探测，别一直停在窗口级
        if (_elementSelection.Target == ElementSelectionTarget.WindowSubElement
            && _elementSelection.Path.Count <= 1)
        {
            ScheduleControlProbe();
        }

        if (_elementSelection.HasSelection)
            ShowCurrentHit();
    }

    /// <summary>Tab：整窗 ↔ 子元素；Ctrl+Tab：循环重叠窗口。</summary>
    private bool HandleTabKey(bool control)
    {
        if (_hasSelection || OcrLayer.IsVisible)
            return false;

        if (control)
        {
            CycleWindowCandidate();
            return true;
        }

        if (_currentCandidate is not null)
        {
            ToggleElementTarget();
            return true;
        }

        return false;
    }

    /// <summary>Esc 的逐级退出逻辑；键盘钩子和正常 OnKeyDown 共用。</summary>
    private bool HandleEscape()
    {
        if (_angleDraft.IsActive || _anglePhase != AnglePhase.None)
        {
            CancelAngleDraft();
            return true;
        }

        // 正在拖选区/拖标注/画一半：先取消这次操作，不关窗口
        if (CancelActiveDrag())
            return true;

        if (_isRecognizing)
        {
            _ocrCancellation?.Cancel();
            return true;
        }

        if (OcrLayer.IsVisible)
        {
            ClearOcrOverlay();
            return true;
        }

        Action = CaptureAction.None;
        Close();
        return true;
    }

    /// <summary>
    /// 无选区时滚轮在命中路径内逐级切换：向上滚放大到父容器，向下滚收紧到子元素。
    /// 整窗模式下滚轮交给系统/其它逻辑，不改变选区。
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_hasSelection
            || OcrLayer.IsVisible
            || _mode != DragMode.None
            || _elementSelection.Target != ElementSelectionTarget.WindowSubElement
            || !_elementSelection.HasSelection)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        var delta = e.Delta.Y > 0 ? 1 : e.Delta.Y < 0 ? -1 : 0;

        if (delta == 0 || !_elementSelection.Step(delta))
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        ShowCurrentHit();
        e.Handled = true;
    }

    /// <summary>单击候选：当前路径层优先，否则整个窗口。</summary>
    private void ApplyCandidate(WindowCandidate candidate, UiElementCandidate? uiCandidate)
    {
        // 窗口层用 DWM 矩形，UIA 根矩形可能少一圈
        var bounds = uiCandidate is not null && uiCandidate.ControlType != "窗口"
            ? uiCandidate.Bounds
            : candidate.Bounds;

        ApplyRect(bounds);
    }

    private void ApplyRect(PixelRect bounds)
    {
        var x = (bounds.X - _origin.X) / _scaling;
        var y = (bounds.Y - _origin.Y) / _scaling;

        var left = Math.Clamp(x, 0, Math.Max(0, Width - 1));
        var top = Math.Clamp(y, 0, Math.Max(0, Height - 1));
        var right = Math.Clamp(x + bounds.Width / _scaling, left + 1, Width);
        var bottom = Math.Clamp(y + bounds.Height / _scaling, top + 1, Height);

        _selection = new Rect(left, top, right - left, bottom - top);

        if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            return;

        _hasSelection = true;
        HideCandidate();
        UpdateSelectionVisuals();
        ShowActionBar(_selection);
        UpdateHandles();
        UpdateSizeBadge(_selection);
        Cursor = MoveCursor;

        _uiProbe?.ReleaseTrees();
    }

    /// <summary>细化专用探测实例：独立 MTA 线程，不阻塞前景 hover 响应。</summary>
    private UiElementProbe RefinementProbe => _refinementProbe ??= new UiElementProbe();

    /// <summary>
    /// 把探测投递到 UIA 线程。和 snow-shot 一样分两段：
    /// 1. 悬停先用 170ms 预算给一条粗路径，绝不卡住鼠标；
    /// 2. 光标停住、且粗路径还没走到底时，再用 1.5s 预算细化，
    ///    途中每 40ms 发布一次更深的路径，看起来是「框逐渐收紧」。
    /// 浏览器第一次查询才懒启用无障碍树，所以没命中时还会静止重试。
    /// </summary>
    private void ScheduleControlProbe()
    {
        // 整窗模式不需要元素探测
        if (_elementSelection.Target == ElementSelectionTarget.Window)
            return;

        if (_hasSelection || OcrLayer.IsVisible || _currentCandidate is null)
            return;

        // 同一窗口正在探：让它跑完，结束后会按最新光标再命中。
        // 用任务是否完成判断，而不是布尔标记——点空白会推进 generation，
        // 旧任务不敢清标记，用布尔会把后续探测永久锁死。
        //
        // 细化阶段例外：细化最长 1.5s，光标一动就必须放弃，
        // 否则复杂页面里下一个位置要排队等它跑完，看起来像卡住。
        var busy = _probeTask is { IsCompleted: false }
                   && _probingWindow == _currentCandidate.Handle;

        if (busy && !_refining)
            return;

        var generation = ++_probeGeneration;

        // 旧细化已被 generation 取消；新探测从干净状态开始
        _refining = false;
        _probingWindow = _currentCandidate.Handle;
        _probeTask = ProbeAsync(generation, _currentCandidate);
    }

    private async Task ProbeAsync(int generation, WindowCandidate window)
    {
        // 只选了 MSAA：不走 UIA，直接用 MSAA 命中
        if (!UsesUia)
        {
            await ProbeMsaaOnlyAsync(generation, window);
            return;
        }

        var probe = _uiProbe;

        if (probe is null)
            return;

        // 粗框也走 UI 线程：探测线程只负责 Report。
        // 光标在细化途中移开时，把迟到的路径丢掉，避免框停在旧位置。
        var latestPoint = WindowCandidates.GetCursorPosition();
        var preview = new Progress<IReadOnlyList<UiElementCandidate>>(path =>
        {
            if (IsProbeCurrent(generation, window) && !CursorMovedSince(latestPoint))
                ApplyHitPath(path);
        });

        // 细化阶段只接受“旧路径的尾部扩展”，避免重建树后框跳到另一棵子树
        var refinementPreview = new Progress<IReadOnlyList<UiElementCandidate>>(path =>
        {
            if (IsProbeCurrent(generation, window) && !CursorMovedSince(latestPoint))
                ApplyRefinementPath(path);
        });

        try
        {
            var rehits = 0;
            var browserRetries = 0;

            while (true)
            {
                var point = WindowCandidates.GetCursorPosition();
                latestPoint = point;
                _probePoint = point;

                // Chrome/Edge 的网页无障碍要收到一次 IAccessible 请求才会建树。
                // 这里只做激活，不使用 MSAA 的选区结果。
                if (!UsePrewarmedTree
                    && IsBrowserLike(window)
                    && _accessibilityActivated.Add(window.Handle)
                    && _msaaProbe is { } activation)
                {
                    try
                    {
                        await activation.ProbeAsync(
                            window.Handle,
                            point,
                            UiElementProbe.RefinementBudget,
                            () => generation == Volatile.Read(ref _probeGeneration),
                            window.Title);
                    }
                    catch
                    {
                    }

                    await Task.Delay(120);
                    if (!IsProbeCurrent(generation, window))
                        return;
                }

                // worker 在真正遍历前会再看一次 generation，过时的任务直接返回
                var foreground = await probe.ProbeAsync(
                    window.Handle,
                    point,
                    UiElementProbe.ForegroundBudget,
                    preview,
                    () => generation == Volatile.Read(ref _probeGeneration),
                    window.Title,
                    refreshTree: !UsePrewarmedTree
                                 && (IsBrowserLike(window) || browserRetries > 0),
                    rawView: false);

                if (!IsProbeCurrent(generation, window))
                    return;

                if (foreground.Reason == UiElementStopReason.Cancelled)
                    return;

                if (CursorMovedSince(point))
                {
                    if (++rehits <= 3)
                        continue;

                    return;
                }

                var result = foreground;
                var needsRefinement = !UsePrewarmedTree
                    && (foreground.CanRefine
                        || (IsCoarsePath(foreground)
                            && IsBrowserLike(window)
                            && browserRetries == 0));

                // 和 snow-shot 的 AccessibilityPending 类似：浏览器首次 UIA 查询
                // 常常只给窗口/窗格，等树初始化完再走一次细化预算。
                if (needsRefinement)
                {
                    if (foreground.Path.Count > 0)
                        ApplyHitPath(foreground.Path);

                    _refining = true;
                    var refinementProbe = RefinementProbe;

                    try
                    {
                        // 和 snow-shot 一样让前景框先稳定 80ms；期间光标一动就放弃细化
                        await Task.Delay(80);
                        if (!IsProbeCurrent(generation, window))
                            return;

                        SelectorTrace.Write($"refine start hwnd=0x{window.Handle.ToInt64():X} point={point}");

                        result = await refinementProbe.ProbeAsync(
                            window.Handle,
                            point,
                            UiElementProbe.RefinementBudget,
                            refinementPreview,
                            () => generation == Volatile.Read(ref _probeGeneration),
                            window.Title,
                            refreshTree: true,
                            rawView: IsBrowserLike(window));

                        SelectorTrace.Write(
                            $"refine result path={Describe(result.Path)} reason={result.Reason}");
                    }
                    finally
                    {
                        refinementProbe.ReleaseTrees();

                        if (generation == Volatile.Read(ref _probeGeneration))
                            _refining = false;
                    }

                    if (!IsProbeCurrent(generation, window))
                        return;

                    if (result.Reason == UiElementStopReason.Cancelled)
                        return;

                    if (CursorMovedSince(point))
                    {
                        if (++rehits <= 3)
                            continue;

                        return;
                    }

                    // 细化只在旧路径上向外扩展；不兼容就保留当前层
                    if (result.Path.Count > 1)
                        ApplyRefinementPath(result.Path);
                }
                else if (result.Path.Count > 0)
                {
                    ApplyHitPath(result.Path);
                }

                // 浏览器无障碍树是异步建立的：窗格/文档/分组都还不算命中，
                // 隔 300ms 再用前景预算探一次，最多约 3 秒；鼠标一动就重新开始。
                if (!UsePrewarmedTree
                    && IsBrowserLike(window)
                    && IsCoarsePath(result)
                    && browserRetries < 8)
                {
                    browserRetries++;
                    SelectorTrace.Write(
                        $"browser retry {browserRetries} path={Describe(result.Path)}");
                    await Task.Delay(300);
                    if (!IsProbeCurrent(generation, window))
                        return;

                    continue;
                }

                return;
            }
        }
        catch
        {
            // 探测失败就保持窗口级候选
        }
    }

    /// <summary>只走 MSAA：没有 UIA 的两段式，单次预算内给结果。</summary>
    private async Task ProbeMsaaOnlyAsync(int generation, WindowCandidate window)
    {
        if (_msaaProbe is not { } probe)
            return;

        try
        {
            var rehits = 0;

            while (true)
            {
                var point = WindowCandidates.GetCursorPosition();

                var result = await probe.ProbeAsync(
                    window.Handle,
                    point,
                    TimeSpan.FromMilliseconds(600),
                    () => generation == Volatile.Read(ref _probeGeneration),
                    window.Title);

                if (!IsProbeCurrent(generation, window))
                    return;

                if (result.Reason == UiElementStopReason.Cancelled)
                    return;

                if (CursorMovedSince(point))
                {
                    if (++rehits <= 3)
                        continue;

                    return;
                }

                if (result.Path.Count > 1)
                    ApplyHitPath(result.Path);

                return;
            }
        }
        catch
        {
            // MSAA 失败就保持窗口级候选
        }
    }

    private bool UsesUia =>
        !string.Equals(CaptureElementApi, "msaa", StringComparison.OrdinalIgnoreCase);

    /// <summary>浏览器 / Electron / UWP：首次 UIA 树可能异步初始化。</summary>
    private static bool IsBrowserLike(WindowCandidate window) => window.ClassName is
        "Chrome_WidgetWin_1" or "Chrome_WidgetWin_0" or "MozillaWindowClass"
        or "ApplicationFrameWindow" or "CefWebView" or "CefBrowserWindow" or "WebViewHost";

    /// <summary>UIA 只给到窗口/文档/窗格/分组，说明还没走到具体控件。</summary>
    private static bool IsCoarsePath(UiElementProbeResult result)
    {
        if (result.Path.Count <= 1)
            return true;

        return result.Path[0].ControlType is "文档" or "窗格" or "分组" or "窗口" or "应用";
    }

    private static bool CursorMovedSince(PixelPoint point)
    {
        var latest = WindowCandidates.GetCursorPosition();
        return Math.Abs(latest.X - point.X) > 2 || Math.Abs(latest.Y - point.Y) > 2;
    }

    private bool IsProbeCurrent(int generation, WindowCandidate window) =>
        generation == Volatile.Read(ref _probeGeneration)
        && _currentCandidate?.Handle == window.Handle
        && !_hasSelection;

    /// <summary>方向键微调：选区和标注一起反向平移，保持屏幕位置，并进撤销栈。</summary>
}
