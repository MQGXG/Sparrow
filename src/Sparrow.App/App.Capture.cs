// App 分部类：截图主流程与截图偏好保存。
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Sparrow.App.History;
using Sparrow.App.Overlays;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;
using Sparrow.App.Settings;
using Sparrow.App.Shell;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App;

public partial class App
{
    /// <summary>
    /// 截图入口：选区 → 动作（识别 / 复制 / 保存 / 固定 / 滚动）。
    /// 截图是宿主能力，OCR 仍然是普通插件——宿主只是替它准备 ImageInput。
    /// </summary>
    private async void CaptureAndRecognize()
    {
        if (_palette is null || _paletteViewModel is null || _catalog is null)
            return;

        // 浏览器被遮罩覆盖后 Chrome 会丢掉网页无障碍树，所以先在无遮挡时预建 UIA 树。
        UiElementProbe? prewarmedProbe = null;
        SelectorTrace.Reset();

        try
        {
            var cursor = WindowCandidates.GetCursorPosition();
            var candidates = WindowCandidates.Enumerate(IntPtr.Zero);
            var target = WindowCandidates.Pick(candidates, cursor);

            // 预建目标不能只看「最上层」：从 VS 按 F5 后再触发时，最上层往往是 VS，
            // 但浏览器仍在光标下方可见。只预建最上层会漏掉浏览器；遮罩盖住后
            // Chromium 关闭网页无障碍树，就只剩窗格可选。
            var browserTargets = new List<WindowCandidate>();

            if (target is not null && WindowCandidates.IsBrowserLikeWindow(target.Handle))
                browserTargets.Add(target);

            foreach (var candidate in candidates)
            {
                if (browserTargets.Count >= 2)
                    break;

                if (!candidate.Contains(cursor)
                    || !WindowCandidates.IsBrowserLikeWindow(candidate.Handle)
                    || browserTargets.Exists(item => item.Handle == candidate.Handle))
                {
                    continue;
                }

                browserTargets.Add(candidate);
            }

            SelectorTrace.Write(
                $"prewarm scan cursor={cursor} target=0x{target?.Handle.ToInt64():X} " +
                $"class={target?.ClassName} " +
                $"browsers=[{string.Join(",", browserTargets.Select(item => $"0x{item.Handle.ToInt64():X}"))}]");

            if (browserTargets.Count > 0)
            {
                var probe = new UiElementProbe();
                var warmedAny = false;

                foreach (var browser in browserTargets)
                {
                    // 只把光标下最上层的浏览器提到前台；其余保持原状，避免抢焦点/闪烁。
                    if (browser.Handle == target?.Handle)
                    {
                        SetForegroundWindow(browser.Handle);
                        await Task.Delay(300);
                    }

                    // 用它自己的中心点做 IAccessible 激活，点不在窗口内时 Chromium 不会建树。
                    var inside = new PixelPoint(
                        browser.Bounds.X + browser.Bounds.Width / 2,
                        browser.Bounds.Y + browser.Bounds.Height / 2);

                    using (var activation = new MsaaElementProbe())
                    {
                        await activation.ProbeAsync(
                            browser.Handle,
                            inside,
                            TimeSpan.FromMilliseconds(1200),
                            null,
                            null);
                    }

                    await Task.Delay(250);

                    var warmed = await probe.PrewarmAsync(
                        browser.Handle, TimeSpan.FromMilliseconds(1500));

                    SelectorTrace.Write(
                        $"prewarm result hwnd=0x{browser.Handle.ToInt64():X} ok={warmed}");

                    // selector.log 会被下一次截图覆盖，这里把关键结果留在持久的 sparrow-*.log。
                    Log.Info(
                        $"预建 UIA 树：hwnd=0x{browser.Handle.ToInt64():X} " +
                        $"class={browser.ClassName} ok={warmed} debugger={Debugger.IsAttached}");

                    warmedAny |= warmed;
                }

                if (warmedAny)
                    prewarmedProbe = probe;
                else
                    probe.Dispose();
            }
        }
        catch
        {
            // 预建失败就走普通探测
        }

        var capture = new CaptureWindow
        {
            ElementProbe = prewarmedProbe,
            OcrRunner = RunCaptureOcrAsync,
            CopyText = CopyTextToClipboard,
            FreezeOnOpen = true,
            CaptureOcrBoxMode = _settingsViewModel?.CaptureOcrBoxMode ?? OcrBoxMode.Hover,
            NoteColorValue = ParseNoteColor(_appSettings.AnnotationColor),
            NoteStrokeWidth = _appSettings.AnnotationWidth,
            NoteMosaicBlur = _appSettings.AnnotationMosaicBlur,
            SizeInPixels = _appSettings.CaptureSizeUnit != "dip",
            ExportCornerRadius = _appSettings.CaptureCornerRadius,
            CaptureToolPositions = _appSettings.CaptureToolPositions,
            CaptureToolHidden = _appSettings.CaptureToolHidden,
            CaptureToolbarPlacement = _appSettings.CaptureToolbarPlacement,
            CaptureElementApi = _appSettings.CaptureElementApi,
            CaptureFrameApi = _appSettings.CaptureApi,
            CaptureShowCursor = _appSettings.CaptureShowCursor,
            CaptureRestoreColors = _appSettings.CaptureRestoreColors,
            SelectionBorderColor = _appSettings.SelectionBorderColor
        };

        // 区域录制：录制控制并入截图窗口主工具条（Snow-shot 行为）。
        capture.RecordingSessionStarter = region =>
        {
            // 已经在录了：不允许再开第二个会话（录制中重新截图/选区时点红点会走到这里）。
            if (_activeCaptureWindow is not null)
            {
                _toasts?.Show("已经在录制中。");
                return Task.FromResult<RecordingSession?>(null);
            }

            _activeCaptureWindow = capture;
            var setup = _pendingRecordingSetup ?? RecordingSetup.Default;
            _pendingRecordingSetup = null;
            return CreateRecordingSessionAsync(region, IntPtr.Zero, setup);
        };

        // 区域录制工具条的「录制设置…」：打开完整面板，确认后仍用当前选区开始。
        capture.RecordingSetupRequested = _ =>
        {
            var panel = new RecordingSetupWindow(_appSettings.RecordEncoder, regionOnly: true);

            panel.Finished += setup =>
            {
                if (setup is null)
                    return;

                PersistRecordingEncoder(setup.Encoder);
                _pendingRecordingSetup = setup;
                capture.StartRecordingFromSelection();
            };

            panel.Show();
        };

        capture.RecordingFinished += (path, error, copied) =>
        {
            _activeCaptureWindow = null;

            if (path is null)
            {
                _toasts?.Show(
                    error is null ? "已取消录制。" : $"录制失败：{error}",
                    TimeSpan.FromSeconds(8));
                return;
            }

            var file = new FileInfo(path);
            var sizeMb = file.Exists ? file.Length / 1024.0 / 1024.0 : 0;

            _toasts?.Show(
                copied
                    ? $"录制完成并已复制到剪贴板：{file.Name}（{sizeMb:F1} MB）"
                    : $"录制完成：{file.Name}（{sizeMb:F1} MB）",
                TimeSpan.FromSeconds(5));

            // 复制录屏时不再打断用户去资源管理器。
            if (copied)
                return;

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        };

        // 三槽样式模板：灌进去，关闭时读回
        for (var index = 0; index < capture.Presets.Length
             && index < _appSettings.AnnotationPresets.Count; index++)
        {
            capture.Presets[index] = _appSettings.AnnotationPresets[index];
        }

        capture.OcrBoxModeChanged += mode =>
            _settingsViewModel?.SetCaptureOcrBoxMode(OcrBoxModes.Format(mode));
        capture.Show();
        capture.Activate();

        // 开发/自动化：SPARROW_CAPTURE_RECORD=1 时直接用固定区域开始录制。
        if (Environment.GetEnvironmentVariable("SPARROW_CAPTURE_RECORD") == "1")
            capture.StartRecordingNow(new PixelRect(200, 200, 800, 600));

        // 窗口先出来再读语言列表：首次会顺手加载插件，别让它卡在显示之前
        capture.ConfigureTranslateTargets(TranslateTargets());
        capture.ConfigureTranslateEngines(TranslateEngines());
        capture.ConfigureCaptureShortcuts(new CaptureWindow.CaptureShortcutConfig(
            _appSettings.CaptureRecognizeKey,
            _appSettings.CaptureTranslateKey,
            _appSettings.CaptureCopyKey,
            _appSettings.CaptureSaveKey,
            _appSettings.CapturePinKey,
            _appSettings.CaptureScrollKey,
            _appSettings.CaptureColorKey,
            _appSettings.CaptureOcrBoxKey));
        Log.Info($"截图窗口已显示：visible={capture.IsVisible}，title={capture.Title}，{MemoryText()}");

        await WaitForClosedAsync(capture);
        Log.Info($"截图窗口已关闭：{MemoryText()}");

        // 预建的 UIA 会话只在本次截图里用；关窗后立刻还给系统
        prewarmedProbe?.Dispose();

        // 记住这次的标注样式和尺寸单位，下次截图直接沿用
        SaveCapturePreferences(capture);

        // 用户取消截图或走了别的动作：丢弃暂存的录制参数，避免下次全屏误用。
        if (capture.Action != CaptureAction.Record)
            _pendingRecordingSetup = null;

        // 旧 OCR 插件只返回文本：窗口已关，直接回退到面板，避免重复识别
        if (capture.FallbackText is { Length: > 0 } fallback)
        {
            _paletteViewModel.ShowExternalResult("截图识别", fallback);
            _paletteViewModel.CopyResult();
            return;
        }

        // 录制选区：已在截图窗口内完成或取消，结果由 RecordingFinished 处理。
        if (capture.Action == CaptureAction.Record)
            return;

        if (capture.Result is not { Length: > 0 } png)
            return;

        if (capture.Action == CaptureAction.Scroll)
        {
            await RunScrollCaptureAsync(capture.SelectionRect, capture.CreateScrollControl());
            return;
        }

        await HandleCaptureActionAsync(png, capture.Action);
    }

    /// <summary>截图窗口关掉后，把标注样式和尺寸单位写回设置（变了才写）。</summary>
    private void SaveCapturePreferences(CaptureWindow capture)
    {
        var color = $"#{capture.NoteColorValue & 0xFFFFFF:X6}";
        var unit = capture.SizeInPixels ? "px" : "dip";
        var presets = capture.Presets.Where(preset => preset is not null).Cast<AnnotationPreset>().ToList();
        var toolPositions = capture.CaptureToolPositions.ToList();
        var toolHidden = capture.CaptureToolHidden.ToList();
        var toolbarPlacement = capture.CaptureToolbarPlacement.ToList();

        if (color == _appSettings.AnnotationColor
            && Math.Abs(capture.NoteStrokeWidth - _appSettings.AnnotationWidth) < 0.01
            && capture.NoteMosaicBlur == _appSettings.AnnotationMosaicBlur
            && unit == _appSettings.CaptureSizeUnit
            && Math.Abs(capture.ExportCornerRadius - _appSettings.CaptureCornerRadius) < 0.01
            && presets.SequenceEqual(_appSettings.AnnotationPresets)
            && toolPositions.SequenceEqual(_appSettings.CaptureToolPositions)
            && toolHidden.SequenceEqual(_appSettings.CaptureToolHidden)
            && toolbarPlacement.SequenceEqual(_appSettings.CaptureToolbarPlacement))
        {
            return;
        }

        _appSettings = _appSettings with
        {
            AnnotationColor = color,
            AnnotationWidth = capture.NoteStrokeWidth,
            AnnotationMosaicBlur = capture.NoteMosaicBlur,
            CaptureSizeUnit = unit,
            CaptureCornerRadius = capture.ExportCornerRadius,
            AnnotationPresets = presets,
            CaptureToolPositions = toolPositions,
            CaptureToolHidden = toolHidden,
            CaptureToolbarPlacement = toolbarPlacement
        };

        try
        {
            _settingsStore?.Save(_appSettings);
        }
        catch (Exception ex)
        {
            Log.Warn($"保存截图样式失败：{ex.Message}");
        }
    }

    private static uint ParseNoteColor(string? value) =>
        uint.TryParse(
            value?.TrimStart('#'),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var rgb)
            ? 0xFF000000u | (rgb & 0xFFFFFF)
            : 0xFFE5A44Cu;
}
