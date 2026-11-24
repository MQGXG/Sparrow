// CaptureWindow 分部类：OCR / 翻译流程与结果浮层。
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
    private async void OnRecognizeClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.Recognize);

    private void OnCopyAllClick(object? sender, RoutedEventArgs e) => CopyAllText();

    /// <summary>
    /// 动作触发时才截图：选区在松手后还可以移动/拉伸，
    /// 直到用户点识别、复制、保存、固定或滚动。
    /// </summary>
    private async Task ActAsync(CaptureAction action)
    {
        if (_isActing || _isRecognizing || !_hasSelection)
            return;

        if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            return;

        _isActing = true;

        try
        {
            var png = await CaptureSelectionAsync(action);
            if (png is null)
            {
                Action = CaptureAction.None;
                Close();
                return;
            }

            // 抓图期间用户可能已经按 Esc 关窗
            if (!IsVisible)
                return;

            // 标注合成进导出的图：复制 / 保存 / 固定 / 识别拿到的都是带标注的版本
            Result = action == CaptureAction.Scroll
                ? png
                : await Task.Run(() => ComposeResult(png));

            Action = action;

            if (action == CaptureAction.Recognize && OcrRunner is not null)
            {
                await RunOcrAsync();
                return;
            }

            Close();
        }
        finally
        {
            _isActing = false;
        }
    }

    private async Task<byte[]?> CaptureSelectionAsync(CaptureAction action)
    {
        var rect = _selection;
        var x = (int)Math.Round(rect.X * _scaling);
        var y = (int)Math.Round(rect.Y * _scaling);
        var width = (int)Math.Round(rect.Width * _scaling);
        var height = (int)Math.Round(rect.Height * _scaling);

        SelectionRect = new PixelRect(_origin.X + x, _origin.Y + y, width, height);

        // 有冻结帧：直接从冻结图裁剪，不再抓屏；滚动截图必须用实时画面
        if (action != CaptureAction.Scroll && _frozenPixels is not null)
        {
            var cropped = ScreenCapture.Crop(
                _frozenPixels,
                _frozenWidth,
                _frozenHeight,
                x,
                y,
                width,
                height);

            return cropped is null ? null : ScreenCapture.EncodePng(cropped, width, height);
        }

        // 退回实时抓屏（滚动截图 / 冻结失败）
        Opacity = 0;
        try
        {
            await Task.Delay(80);

            var absoluteX = _origin.X + x;
            var absoluteY = _origin.Y + y;

            return await Task.Run(() =>
                ScreenCapture.CaptureStable(absoluteX, absoluteY, width, height));
        }
        finally
        {
            Opacity = 1;
        }
    }

    private async Task RunOcrAsync(CaptureOcrOptions? options = null)
    {
        // 没有注入 runner 或还没截图时，退回旧流程
        if (OcrRunner is null || Result is not { Length: > 0 } png)
        {
            Action = CaptureAction.Recognize;
            Close();
            return;
        }

        if (_isRecognizing)
            return;

        _isRecognizing = true;
        _ocrCancellation = new CancellationTokenSource();
        _translateTarget = options?.TranslateTarget;
        RecognizeButton.IsEnabled = false;
        TranslateButton.IsEnabled = false;
        OcrStatus.IsVisible = true;
        OcrStatus.Text = options?.TranslateTarget is { Length: > 0 } target
            ? $"正在翻译（{target}）…"
            : "正在识别…";

        try
        {
            var progress = new Progress<IReadOnlyList<OcrTextLine>>(ApplyPartialOcrLines);
            var result = await OcrRunner(
                png, options ?? new CaptureOcrOptions(), progress, _ocrCancellation.Token);

            if (!string.IsNullOrEmpty(result.Error))
            {
                ShowOcrError(result.Error);
                return;
            }

            // 旧插件只返回文本：关窗，让宿主回退到面板
            if (result.IsFallback)
            {
                FallbackText = result.Text;
                Action = CaptureAction.Recognize;
                Close();
                return;
            }

            ShowOcrResult(result);
        }
        catch (OperationCanceledException)
        {
            OcrStatus.Text = "已取消";
        }
        catch (Exception ex)
        {
            ShowOcrError($"识别失败：{ex.Message}");
        }
        finally
        {
            _isRecognizing = false;
            RecognizeButton.IsEnabled = true;
            TranslateButton.IsEnabled = true;
            _ocrCancellation?.Dispose();
            _ocrCancellation = null;
        }
    }

    /// <summary>
    /// 「翻译」菜单选中语言后：先按当前选区取图，再走同一条 OCR 链路，只是多带目标语言。
    /// 不能直接调 RunOcrAsync——那时 Result 还没生成。
    /// </summary>
    private async Task RunTranslateAsync(string target)
    {
        if (_isActing || _isRecognizing || !_hasSelection)
            return;

        if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            return;

        _isActing = true;

        try
        {
            var png = await CaptureSelectionAsync(CaptureAction.Recognize);
            if (png is null)
            {
                Action = CaptureAction.None;
                Close();
                return;
            }

            if (!IsVisible)
                return;

            Result = await Task.Run(() => ComposeResult(png));
            Action = CaptureAction.Recognize;

            await RunOcrAsync(new CaptureOcrOptions(target, _translateEngine));
        }
        finally
        {
            _isActing = false;
        }
    }

    /// <summary>把行框叠在选区上；坐标按主屏缩放换算回 DIP。</summary>
    internal void ShowOcrResult(CaptureOcrResult result, bool partial = false)
    {
        _ocrText = result.Text;
        OcrLayer.Children.Clear();
        _ocrBoxes.Clear();
        _selectedOcrBoxes.Clear();
        _ocrLines.Clear();
        _ocrMarks.Clear();
        ClearOcrCharSelection();
        HideOcrMarkMenu();
        _hoveredOcrBox = null;
        HandleLayer.IsVisible = false;

        var left = Canvas.GetLeft(Selection);
        var top = Canvas.GetTop(Selection);
        if (double.IsNaN(left)) left = _selection.X;
        if (double.IsNaN(top)) top = _selection.Y;

        OcrLayer.Width = Selection.Width;
        OcrLayer.Height = Selection.Height;
        Canvas.SetLeft(OcrLayer, left);
        Canvas.SetTop(OcrLayer, top);
        OcrLayer.IsVisible = true;

        // OCR 阶段不会再探测控件框
        _uiProbe?.ReleaseTrees();

        foreach (var line in result.Lines)
        {
            var height = Math.Max(line.Height / _scaling, 8);

            var box = new Rectangle
            {
                Width = Math.Max(1, line.Width / _scaling + OcrLinePadding * 2),
                Height = height + OcrLinePadding * 2,
                Fill = LineFill,
                Stroke = LineStroke,
                StrokeThickness = 1,
                RadiusX = 2,
                RadiusY = 2,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = line.Text.Trim()
            };

            box.PointerEntered += OnOcrLineEntered;
            box.PointerExited += OnOcrLineExited;

            // 字符级选择和点击都在 OcrLayer 上统一处理，行框只负责外观
            var dipLine = new OcrTextLine(
                line.Text,
                line.X / _scaling,
                line.Y / _scaling,
                line.Width / _scaling,
                line.Height / _scaling,
                line.Translation);
            _ocrLines.Add((dipLine, OcrTextLayout.Split(dipLine)));

            OcrLayer.Children.Add(box);
            _ocrBoxes.Add(box);
            Canvas.SetLeft(box, line.X / _scaling - OcrLinePadding);
            Canvas.SetTop(box, line.Y / _scaling - OcrLinePadding);
        }

        ToolTip.SetTip(RecognizeButton, "重新识别选区文字（Enter / R）");
        _ocrActionsAvailable = true;
        OcrStatus.IsVisible = true;
        InvalidateToolbarMetrics();
        ApplyDensityVisibility();
        ApplyOcrBoxMode();
        UpdateTranslatedLayer(allowWarning: !partial);
    }

    /// <summary>
    /// 翻译过程的中间结果：第一帧先把行框画出来，之后随译文增长重画译文层。
    /// 和 snow-shot 一样，用户看到的是「一行一行 / 一个词一个词」落上去，而不是等整段完成。
    /// </summary>
    private void ApplyPartialOcrLines(IReadOnlyList<OcrTextLine> lines)
    {
        // 窗口已关（用户取消/又截了一张）就别再动控件
        if (!IsVisible || lines.Count == 0)
            return;

        if (_ocrLines.Count != lines.Count)
        {
            // 还没有行框：先把框画出来（此时译文为空，不能提示「没对齐」）
            ShowOcrResult(new CaptureOcrResult(string.Empty, lines), partial: true);
            OcrStatus.Text = "正在翻译…";
            _lastPartialRender = Environment.TickCount64;
            return;
        }

        // 一 token 一重绘太贵，80ms 一帧足够看出「逐词出现」
        var now = Environment.TickCount64;
        if (now - _lastPartialRender < 80)
            return;

        _lastPartialRender = now;

        for (var index = 0; index < lines.Count; index++)
        {
            _ocrLines[index] = (
                _ocrLines[index].Line with { Translation = lines[index].Translation },
                _ocrLines[index].Chars);
        }

        UpdateTranslatedLayer(allowWarning: false, buildCompare: false);
        OcrStatus.Text = "正在翻译…";
    }

    /// <summary>
    /// 有按行译文时生成"译文图"，盖在选区上；没有就什么都不做。
    /// 底图用这次识别时的截图（`Result`），尺寸和导出一致。
    /// </summary>
    private void UpdateTranslatedLayer(bool allowWarning = true, bool buildCompare = true)
    {
        _translatedPng = null;
        _comparePng = null;
        _compareView = false;
        _showOriginalView = false;
        TransLayer.Source = null;
        TransLayer.IsVisible = false;
        TransRow.IsVisible = false;
        TransTargetText.Text = _translateTarget is { Length: > 0 } target
            ? $"译文 · {target}"
            : "译文";

        var lines = _ocrLines
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Line.Translation))
            .Select(entry => new TranslatedImageRenderer.LineBox(
                new Rect(entry.Line.X, entry.Line.Y, entry.Line.Width, entry.Line.Height),
                entry.Line.Text,
                entry.Line.Translation))
            .ToList();

        if (lines.Count == 0)
        {
            // 用户要了翻译但一行都没落上：多半是没有可用引擎，或模型没按编号返回
            if (allowWarning && !string.IsNullOrEmpty(_translateTarget))
                OcrStatus.Text = "没有拿到可对齐的译文，已保留原文（检查翻译引擎）";
            return;
        }

        if (Result is not { Length: > 0 } cropPng)
            return;

        using var crop = SKBitmap.Decode(cropPng);
        if (crop is null)
            return;

        // 对不上就不画：宁可不叠译文，也不把整段盖在原文上
        var png = TranslatedImageRenderer.Render(crop, _scaling, lines);
        if (png is null)
        {
            OcrStatus.Text = "译文未能按行对齐，已保留原文";
            return;
        }

        _translatedPng = png;

        // 左右对比图：原图和译文图并排（显示时按窗口可用区域等比放大并居中）
        if (buildCompare)
        {
            using var translated = SKBitmap.Decode(png);
            if (translated is not null)
                _comparePng = TranslatedImageRenderer.BuildCompare(crop, translated);
        }

        ShowOriginalButton.IsVisible = true;
        TransCompareButton.IsVisible = _comparePng is not null;
        TransRow.IsVisible = true;
        ApplyTranslationView();
    }

    /// <summary>从"原文 + —— 译文（x）——"里取出译文部分；没有返回 null。</summary>
    internal static string? ExtractTranslatedText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var start = text.IndexOf("—— 译文（", StringComparison.Ordinal);
        if (start < 0)
            return null;

        var end = text.IndexOf("）——", start, StringComparison.Ordinal);
        if (end < 0)
            return null;

        var body = text[(end + 3)..].Trim();
        return body.Length == 0 ? null : body;
    }

    /// <summary>按当前视图状态同步译文层 / 行框 / 按钮。</summary>
    private void ApplyTranslationView()
    {
        var hasTranslation = _translatedPng is not null;
        var showingCompare = hasTranslation && _compareView && _comparePng is not null;

        if (!hasTranslation)
        {
            TransLayer.Source = null;
            TransLayer.IsVisible = false;
            return;
        }

        // 对比图是两张并排：塞进选区会被压扁，改成「按窗口可用区域等比放大」并居中到选区
        TransLayer.Stretch = Stretch.Fill;
        TransLayer.Source = DecodePng(showingCompare ? _comparePng : _translatedPng);
        TransLayer.IsVisible = showingCompare || !_showOriginalView;
        OcrLayer.IsVisible = !showingCompare && _showOriginalView;

        if (showingCompare)
            PlaceCompareLayer(_comparePng!);
        else
            PlaceTranslatedLayer();

        SetSelected(ShowOriginalButton, _showOriginalView);
        ShowOriginalText.Text = _showOriginalView ? "显示译文" : "显示原图";
        ToolTip.SetTip(ShowOriginalButton, _showOriginalView ? "显示译文" : "显示原图");

        SetSelected(TransCompareButton, showingCompare);
        ToolTip.SetTip(TransCompareButton, showingCompare ? "退出对比" : "左右对比");

        OcrStatus.Text = showingCompare
            ? "左右对比 · 左原文 / 右译文"
            : _showOriginalView ? "原文视图" : "译文视图 · 点眼睛切回原文";
    }

    /// <summary>译文图铺满选区：和原图逐像素重合，切换时不跳动。</summary>
    private void PlaceTranslatedLayer()
    {
        TransLayer.Width = _selection.Width;
        TransLayer.Height = _selection.Height;
        Canvas.SetLeft(TransLayer, _selection.X);
        Canvas.SetTop(TransLayer, _selection.Y);
    }

    /// <summary>
    /// 对比图居中放大：宽高受窗口可用区域限制，等比缩放到放得下，
    /// 再以选区中心为基准摆放——不再被压进选区里变成两张小邮票。
    /// </summary>
    private void PlaceCompareLayer(byte[] comparePng)
    {
        using var bitmap = SKBitmap.Decode(comparePng);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            return;

        // 物理像素 → DIP
        var width = bitmap.Width / Math.Max(_scaling, 0.01);
        var height = bitmap.Height / Math.Max(_scaling, 0.01);

        var maxWidth = Math.Max(200, Width - 96);
        var maxHeight = Math.Max(150, Height - 220);   // 给上下工具条留位置
        var factor = Math.Min(1.0, Math.Min(maxWidth / width, maxHeight / height));

        var targetWidth = width * factor;
        var targetHeight = height * factor;

        var center = _selection.Center;
        var left = Math.Clamp(center.X - targetWidth / 2, 48, Math.Max(48, Width - targetWidth - 48));
        var top = Math.Clamp(center.Y - targetHeight / 2, 96, Math.Max(96, Height - targetHeight - 96));

        TransLayer.Width = targetWidth;
        TransLayer.Height = targetHeight;
        Canvas.SetLeft(TransLayer, left);
        Canvas.SetTop(TransLayer, top);
    }

    private static WriteableBitmap? DecodePng(byte[]? png)
    {
        if (png is null)
            return null;

        using var stream = new MemoryStream(png);
        return WriteableBitmap.Decode(stream);
    }

    private void OnTransCompareClick(object? sender, RoutedEventArgs e)
    {
        if (_comparePng is null)
            return;

        _compareView = !_compareView;
        _showOriginalView = false;
        ApplyTranslationView();
    }

    /// <summary>退出译文视图：回到识别框，译文图保留但不再显示。</summary>
    private void OnTransCloseClick(object? sender, RoutedEventArgs e)
    {
        _translatedPng = null;
        _showOriginalView = false;
        _compareView = false;
        _comparePng = null;
        TransLayer.Source = null;
        TransLayer.IsVisible = false;
        TransRow.IsVisible = false;
        OcrLayer.IsVisible = true;
        ApplyTranslationView();
    }

    /// <summary>只复制译文（不含原文与分隔标题）。</summary>
    private void OnTransCopyClick(object? sender, RoutedEventArgs e)
    {
        var translation = ExtractTranslatedText(_ocrText);
        if (!string.IsNullOrEmpty(translation))
            CopyText?.Invoke(FormatOcrText(translation));
    }

    private void OnShowOriginalClick(object? sender, RoutedEventArgs e)
    {
        if (_translatedPng is null)
            return;

        _compareView = false;
        _showOriginalView = !_showOriginalView;
        ApplyTranslationView();
    }

    private void ShowOcrError(string message)
    {
        OcrStatus.IsVisible = true;
        OcrStatus.Text = message;
    }

    private void ClearOcrOverlay()
    {
        OcrLayer.Children.Clear();
        _ocrBoxes.Clear();
        _selectedOcrBoxes.Clear();
        _ocrLines.Clear();
        _ocrMarks.Clear();
        _ocrSelAnchor = null;
        _ocrSelFocus = null;
        _ocrDragging = false;
        _translatedPng = null;
        _comparePng = null;
        _compareView = false;
        _showOriginalView = false;
        TransLayer.Source = null;
        TransLayer.IsVisible = false;
        TransRow.IsVisible = false;
        HideOcrMarkMenu();
        _hoveredOcrBox = null;
        OcrLayer.IsVisible = false;
        _ocrActionsAvailable = false;
        CopyAllButton.IsVisible = false;
        OcrBoxButton.IsVisible = false;
        FormatButton.IsVisible = false;
        PunctuationButton.IsVisible = false;
        OcrStatus.IsVisible = false;
        InvalidateToolbarMetrics();
        ToolTip.SetTip(RecognizeButton, "识别选区文字（Enter / R）");
        _ocrText = null;
        UpdateHandles();
    }

    private void OnOcrBoxModeClick(object? sender, RoutedEventArgs e) => CycleOcrBoxMode();

    private void CycleOcrBoxMode()
    {
        _ocrBoxMode = OcrBoxModes.Next(_ocrBoxMode);
        ApplyOcrBoxMode();
        OcrBoxModeChanged?.Invoke(_ocrBoxMode);
    }

    /// <summary>按模式统一设置行框：全部显示 / 悬停 / 关闭。</summary>
    private void ApplyOcrBoxMode()
    {
        // 图标按钮：状态只体现在 tooltip 和行框可见性上
        ToolTip.SetTip(OcrBoxButton, $"识别框：{OcrBoxModes.Label(_ocrBoxMode)}（B）");
        OcrBoxButton.IsVisible = _ocrActionsAvailable
            && _toolbarDensity < 2
            && OcrLayer.IsVisible;
        OcrLayer.IsHitTestVisible = _ocrBoxMode != OcrBoxMode.Off;
        _hoveredOcrBox = null;

        foreach (var box in _ocrBoxes)
        {
            box.IsVisible = _ocrBoxMode == OcrBoxMode.All;
            ApplyOcrBoxVisual(box, hovered: false);
        }

        if (OcrLayer.IsVisible)
            UpdateOcrStatus();
    }

    private void UpdateOcrStatus()
    {
        var hint = _ocrBoxMode switch
        {
            OcrBoxMode.All => "点行选中 · 双击复制",
            OcrBoxMode.Hover => "悬停行可点",
            _ => "行框已关闭"
        };

        var selected = _selectedOcrBoxes.Count;
        OcrStatus.Text = selected > 0
            ? $"{_ocrBoxes.Count} 行 · 已选 {selected} · Ctrl+C 复制"
            : $"{_ocrBoxes.Count} 行 · {hint}";
    }

    private void OnOcrLayerPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(OcrLayer);

        // 拖选优先：拖动中不参与悬停高亮
        UpdateOcrSelectionDrag(point);

        if (_ocrBoxMode != OcrBoxMode.Hover || _ocrBoxes.Count == 0)
            return;
        Rectangle? hit = null;

        foreach (var box in _ocrBoxes)
        {
            var left = Canvas.GetLeft(box);
            var top = Canvas.GetTop(box);

            if (double.IsNaN(left) || double.IsNaN(top))
                continue;

            // 行框本身很薄，外扩一点更好点
            var rect = new Rect(left - 4, top - 4, box.Width + 8, box.Height + 8);
            if (rect.Contains(point))
            {
                hit = box;
                break;
            }
        }

        if (ReferenceEquals(hit, _hoveredOcrBox))
            return;

        if (_hoveredOcrBox is not null)
        {
            _hoveredOcrBox.IsVisible = false;
            _hoveredOcrBox.Fill = LineFill;
        }

        _hoveredOcrBox = hit;

        if (hit is not null)
        {
            hit.IsVisible = true;
            hit.Stroke = LineHoverStroke;
            hit.StrokeThickness = 2;
        }
    }

    private void OnOcrLayerPointerExited(object? sender, PointerEventArgs e)
    {
        if (_hoveredOcrBox is null)
            return;

        _hoveredOcrBox.IsVisible = false;
        _hoveredOcrBox.Fill = LineFill;
        _hoveredOcrBox.Stroke = LineStroke;
        _hoveredOcrBox.StrokeThickness = 1;
        _hoveredOcrBox = null;
    }
}
