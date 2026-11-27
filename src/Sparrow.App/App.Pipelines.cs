// App 分部类：识别/滚动/保存/贴图/热键等结果管线。
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
    private async Task RecognizeImageAsync(byte[] png)
    {
        if (_palette is null || _paletteViewModel is null || _catalog is null)
            return;

        Log.Info($"截图完成：{png.Length} 字节");

        // 按输入形态找工具，而不是写死 id：加一个「截图翻译」插件就能直接出现在这里
        var entry = _catalog.GetRegistry()
            .FindAccepting(InputShape.Image)
            .FirstOrDefault();

        if (entry is null)
        {
            _paletteViewModel.ShowExternalError("没有能识别图片的工具");
            _palette.Show();
            _palette.Activate();
            return;
        }

        Log.Info($"截图交给工具：{entry.Descriptor.Id}");

        _paletteViewModel.ShowPending("正在识别截图…");
        _palette.Show();
        _palette.Activate();

        try
        {
            var context = new ToolContext
            {
                Input = new ImageInput(png, ImageFormat.Png),
                Source = ToolSource.Screenshot,
                Parameters = BuildParameters(entry),
                Host = entry.Host
            };

            var result = await ToolRunner.ExecuteAsync(entry.Tool, context);

            var text = result.Payload switch
            {
                TextPayload textPayload => textPayload.Text,
                OcrResultPayload ocrPayload => ocrPayload.Text,
                _ => null
            };

            if (result.Success && text is not null)
            {
                _paletteViewModel.ShowExternalResult("截图识别", text);
                _paletteViewModel.CopyResult();
                Log.Info($"截图识别完成：{text.Length} 字符");
            }
            else
            {
                _paletteViewModel.ShowExternalError(result.Error?.Message ?? "识别失败");
                Log.Warn($"截图识别失败：{result.Error?.Message}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("截图识别失败", ex);
            _paletteViewModel.ShowExternalError($"截图识别失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 滚动截图：循环抓帧 → 拼接 → 预览 → 复用普通截图的结果动作。
    ///
    /// 拼接全部发生在宿主，插件仍然只收到一张 ImageInput，
    /// 所以这条功能不需要改 SDK 契约。
    /// </summary>
    private async Task RunScrollCaptureAsync(PixelRect rect, ScrollCaptureControl control)
    {
        if (rect.Width < 8 || rect.Height < 8)
            return;

        using var cts = new CancellationTokenSource();
        var cancelled = false;

        var overlay = new ScrollCaptureWindow(control, rect.Height);
        overlay.Finished += (_, _) => cts.Cancel();
        overlay.Cancelled += (_, _) =>
        {
            cancelled = true;
            cts.Cancel();
        };

        PositionScrollOverlay(overlay, rect);
        overlay.Show();

        // 关闭「捕获界面」时把滚动提示条从取帧里排除，避免拼进结果
        if (!_appSettings.CaptureUiInScroll)
            WindowCaptureExclusion.Exclude(overlay);

        // 自动滚动的滚轮事件发给前台窗口：先把光标移进选区，并把目标窗口切到前台，
        // 结束后还原光标。截图窗口刚关时我们还在前台，这时切目标最可靠。
        var originalCursor = WheelScroller.GetCursorPosition();
        var movedCursor = false;

        if (control.AutoScroll)
        {
            var center = new PixelPoint(
                rect.X + rect.Width / 2,
                rect.Y + rect.Height / 2);

            movedCursor = WheelScroller.MoveCursor(center.X, center.Y);
            WheelScroller.ActivateTargetAt(center);
        }

        byte[]? png;
        try
        {
            png = await Task.Run(
                () => ScrollCaptureService.CaptureAsync(
                    rect.X,
                    rect.Y,
                    rect.Width,
                    rect.Height,
                    control,
                    rows => Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => overlay.SetCaptured(
                            rows,
                            control.Horizontal ? rect.Width : rect.Height)),
                    preview => Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => overlay.SetPreview(preview)),
                    cts.Token),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            png = null;
        }
        finally
        {
            overlay.Close();

            if (movedCursor && originalCursor is { } cursor)
                WheelScroller.MoveCursor(cursor.X, cursor.Y);
        }

        if (cancelled || png is null)
            return;

        Log.Info($"滚动截图完成：{png.Length} 字节，区域 {rect.Width}×{rect.Height}");

        var result = new ScrollResultWindow(png);
        result.Show();
        result.Activate();
        await WaitForClosedAsync(result);

        if (result.Action == CaptureAction.None)
            return;

        await HandleCaptureActionAsync(result.OutputPng ?? png, result.Action);
    }

    /// <summary>把提示条放在选区外（下方放不下就放上方），避免被自己截进去。</summary>
    private static void PositionScrollOverlay(Window overlay, PixelRect selection)
    {
        var center = new PixelPoint(
            selection.X + selection.Width / 2,
            selection.Y + selection.Height / 2);

        var screen = overlay.Screens.ScreenFromPoint(center) ?? overlay.Screens.Primary;
        var scaling = screen?.Scaling ?? 1;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        var width = (int)(320 * scaling);
        // 缩略图最高 340 + 文本/按钮，按最大高度预留位置
        var height = (int)(480 * scaling);

        var areaRight = area.X + area.Width;
        var areaBottom = area.Y + area.Height;
        var minX = area.X + 8;
        var minY = area.Y + 8;
        var maxX = Math.Max(minX, areaRight - width - 8);
        var maxY = Math.Max(minY, areaBottom - height - 8);

        // 优先放选区右侧，避免盖住正在滚动的页面
        var x = selection.X + selection.Width + 12;
        var y = Math.Clamp(selection.Y, minY, maxY);

        if (x > maxX)
        {
            var left = selection.X - width - 12;

            if (left >= minX)
            {
                x = left;
            }
            else
            {
                // 两侧都放不下：回到底部 / 顶部
                x = Math.Clamp(selection.X + selection.Width - width, minX, maxX);
                y = selection.Y + selection.Height + 12;

                if (y > maxY)
                    y = selection.Y - height - 12;

                y = Math.Clamp(y, minY, maxY);
            }
        }

        overlay.Position = new PixelPoint(Math.Clamp(x, minX, maxX), y);
    }

    /// <summary>弹保存对话框，把截图写成 PNG。</summary>
    private async Task SaveImageAsync(byte[] png)
    {
        var owner = (Window?)_palette ?? _workspace;
        if (owner is null)
            return;

        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存截图",
                SuggestedFileName = $"Sparrow-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                DefaultExtension = "png",
                FileTypeChoices =
                [
                    new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }
                ]
            });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
            Log.Info($"截图已保存：{file.Path}");
        }
        catch (Exception ex)
        {
            Log.Error("保存截图失败", ex);
        }
    }

    /// <summary>把截图钉到屏幕上，可同时存在多张。</summary>
    private void PinImage(byte[] png)
    {
        try
        {
            var pin = new PinnedImageWindow(png);
            pin.Closed += (_, _) => _pins.Remove(pin);
            _pins.Add(pin);
            pin.Show();
            Log.Info("贴图已显示");
        }
        catch (Exception ex)
        {
            Log.Error("显示贴图失败", ex);
        }
    }

    /// <summary>
    /// 工具热键 / 托盘菜单的统一执行入口：不打开面板，直接用同一个上下文构建逻辑执行，
    /// 完成后把结果交给面板展示（长时间交互类工具的结果也写进历史）。
    /// </summary>
    /// <summary>
    /// 全局热键：粘贴队列下一条。
    /// 热键在目标应用里按下时，Sparrow 不抢焦点；写剪贴板后注入 Ctrl+V。
    /// </summary>
    private void PasteNextFromQueue()
    {
        var clipboard = _workspaceViewModel?.Clipboard;

        if (clipboard is null || !clipboard.HasQueue)
        {
            _toasts?.Show("粘贴队列为空：先到「剪贴板」页把条目加入队列", TimeSpan.FromSeconds(4));
            return;
        }

        if (!clipboard.PasteNext(simulate: true))
            _toasts?.Show("顺序粘贴失败：下一条没有可粘贴内容", TimeSpan.FromSeconds(4));
    }

    private async Task ExecuteToolHotkeyAsync(string toolId)
    {
        if (_catalog is null || _palette is null || _paletteViewModel is null)
            return;

        // 上一个热键工具还没结束（比如正在拾取窗口）：忽略，避免叠两次执行
        if (Interlocked.Exchange(ref _toolHotkeyRunning, 1) == 1)
            return;

        try
        {
            var entry = _catalog.GetRegistry()
                .Search(null, int.MaxValue)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Descriptor.Id, toolId, StringComparison.Ordinal));

            if (entry is null)
            {
                Log.Warn($"工具热键对应的工具不存在（可能已禁用或卸载）：{toolId}");
                _toasts?.Show($"工具不存在：{toolId}");
                return;
            }

            var (context, error) = _paletteViewModel.CreateContext(entry);
            if (context is null)
            {
                _toasts?.Show(error ?? "无法构造输入");
                return;
            }

            Log.Info($"工具热键执行：{toolId}");

            var startedAt = Stopwatch.GetTimestamp();
            var result = await ToolRunner.ExecuteAsync(entry.Tool, context);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (!result.Success || result.Payload is null)
            {
                Log.Warn($"工具热键执行失败：{toolId}，{result.Error?.Message}");
                _toasts?.Show(result.Error?.Message ?? "执行失败", TimeSpan.FromSeconds(5));
                return;
            }

            var text = PaletteViewModel.FormatPayload(result.Payload);

            // 短结果用 toast 就够；长文本仍然交给面板，方便阅读和复制
            if (text.Length <= 160)
                _toasts?.Show(text);
            else
                _palette.ShowResult(entry.Descriptor.Name, text);

            _history?.Add(new HistoryEntry(
                Guid.NewGuid().ToString("N"),
                entry.Descriptor.Id,
                entry.Descriptor.Name,
                text,
                PaletteViewModel.PreviewInput(context.Input),
                DateTimeOffset.Now,
                (long)elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：轻提示即可，不打扰
            _toasts?.Show("已取消");
        }
        catch (Exception ex)
        {
            Log.Error($"工具热键执行异常：{toolId}", ex);
            _toasts?.Show($"执行异常：{ex.Message}", TimeSpan.FromSeconds(5));
        }
        finally
        {
            Interlocked.Exchange(ref _toolHotkeyRunning, 0);
        }
    }

    private static (uint Modifiers, uint VirtualKey) ResolveHotkey(string? text, string fallback)
    {
        if (HotkeyText.TryParse(text, out var modifiers, out var virtualKey))
            return (modifiers, virtualKey);

        // 配置坏了就退回默认，不让应用因为一个手误的热键起不来
        HotkeyText.TryParse(fallback, out modifiers, out virtualKey);
        return (modifiers, virtualKey);
    }

    private static async Task WaitForClosedAsync(Window window)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClosed(object? _, EventArgs __) => completion.TrySetResult(true);

        window.Closed += OnClosed;

        // 窗口若被 Hide / 异常销毁而从未触发 Closed，无限期等待会把 async void
        // 状态机连同冻结帧、结果 PNG 永久挂住。给一个宽松的兜底，保证状态机
        // 一定会结束；正常关窗走 Closed 分支，超时只在异常路径生效。
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));

        try
        {
            await Task.WhenAny(
                completion.Task,
                Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)).ConfigureAwait(true);
        }
        finally
        {
            // 无论正常关闭还是超时兜底，都断开订阅，避免窗口残留委托。
            window.Closed -= OnClosed;
        }
    }

    /// <summary>预建 UIA 树前把目标浏览器窗口提到前台，Chromium 才会构建网页无障碍树。</summary>
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
