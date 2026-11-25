// CaptureWindow 分部类：OCR 文本选择、复制与文本标记。
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
    private void CopyAllText() => CopyOcrText();

    /// <summary>
    /// 有选中就复制选中的行，没有就复制全文。
    /// 选中行按阅读顺序合并（先按 Y 聚行，行内按 X 排）。
    /// </summary>
    internal void CopyOcrText()    {
        // 译文视图下复制当前看到的译文（和 snow-shot「复制可见内容」一致）
        if (_translatedPng is not null && !_showOriginalView && !_compareView)
        {
            var translation = ExtractTranslatedText(_ocrText);

            if (!string.IsNullOrWhiteSpace(translation))
            {
                var translated = FormatOcrText(translation);
                CopyText?.Invoke(translated);
                OcrStatus.Text = $"已复制译文 · {translated.Length} 字";
                return;
            }
        }

        if (_selectedOcrBoxes.Count > 0)
        {
            var text = MergeOcrText(_selectedOcrBoxes.Select(box => (
                (box.Tag as string) ?? string.Empty,
                new Rect(Canvas.GetLeft(box), Canvas.GetTop(box), box.Width, box.Height))));

            if (string.IsNullOrWhiteSpace(text))
                return;

            text = FormatOcrText(text);
            CopyText?.Invoke(text);
            OcrStatus.Text = $"已复制选中 {_selectedOcrBoxes.Count} 行 · {text.Length} 字";
            return;
        }

        if (string.IsNullOrEmpty(_ocrText))
            return;

        var all = FormatOcrText(_ocrText);
        CopyText?.Invoke(all);
        OcrStatus.Text = $"已复制全文 · {all.Length} 字";
    }

    /// <summary>套用「格式 / 标点」；只改复制出去的文本，行框与译文图不动。</summary>
    internal string FormatOcrText(string text) =>
        OcrTextTransform.Apply(text, _ocrFormatting, _ocrPunctuation);

    /// <summary>把若干行框按阅读顺序合并成文本；同一行内按 X 排，行间换行。</summary>
    internal static string MergeOcrText(IEnumerable<(string Text, Rect Bounds)> boxes)
    {
        var items = boxes
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .OrderBy(item => item.Bounds.Y)
            .ThenBy(item => item.Bounds.X)
            .ToList();

        if (items.Count == 0)
            return string.Empty;

        var lines = new List<List<(string Text, Rect Bounds)>>();
        foreach (var item in items)
        {
            var line = lines.Count > 0 ? lines[^1] : null;
            var tolerance = Math.Max(4, item.Bounds.Height / 2);

            if (line is not null && Math.Abs(item.Bounds.Y - line[0].Bounds.Y) <= tolerance)
                line.Add(item);
            else
                lines.Add([item]);
        }

        return string.Join(
            "\n",
            lines.Select(line => JoinFragments(line
                .OrderBy(item => item.Bounds.X)
                .Select(item => item.Text.Trim())
                .ToList())));
    }

    /// <summary>同一行内的碎片拼接：两侧都是西文才补空格，中文之间不加。</summary>
    private static string JoinFragments(IReadOnlyList<string> parts)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
                continue;

            if (builder.Length > 0 && NeedsSpace(builder[^1], part[0]))
                builder.Append(' ');

            builder.Append(part);
        }

        return builder.ToString();
    }

    private static bool NeedsSpace(char left, char right) => left < 0x2E80 && right < 0x2E80;

    private void OnOcrLineEntered(object? sender, PointerEventArgs e)
    {
        // 悬停只加亮边框，中间保持原图可见
        if (sender is Rectangle box)
            ApplyOcrBoxVisual(box, hovered: true);
    }

    private void OnOcrLineExited(object? sender, PointerEventArgs e)
    {
        if (sender is Rectangle box)
            ApplyOcrBoxVisual(box, hovered: false);
    }

    // ---------- OCR 字符级选择 ----------

    private void OnOcrLayerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(OcrLayer);
        var line = OcrLineAt(point);

        if (line < 0)
        {
            ClearOcrCharSelection();
            HideOcrMarkMenu();
            return;
        }

        _ocrPressPoint = point;
        _ocrPressClickCount = e.ClickCount;
        _ocrDragging = false;
        var index = OcrTextLayout.IndexAt(_ocrLines[line].Chars, point);
        _ocrSelAnchor = (line, index);
        _ocrSelFocus = (line, index);
        HideOcrMarkMenu();
        e.Handled = true;
    }

    private void OnOcrLayerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_ocrSelAnchor is not { } anchor)
            return;

        e.Handled = true;

        // 没有拖动：单击切换整行选中，双击复制这一行
        if (!_ocrDragging)
        {
            if (anchor.Line >= 0 && anchor.Line < _ocrBoxes.Count)
            {
                if (_ocrPressClickCount >= 2)
                {
                    var text = _ocrLines[anchor.Line].Line.Text;
                    CopyText?.Invoke(text);
                    OcrStatus.Text = $"已复制：{Shorten(text)}";
                }
                else
                {
                    ToggleOcrBoxSelection(_ocrBoxes[anchor.Line]);
                }
            }

            ClearOcrCharSelection();
            return;
        }

        if (GetOcrSelectionRanges().Count == 0)
        {
            ClearOcrCharSelection();
            return;
        }

        ShowOcrMarkMenu();
    }

    /// <summary>拖动中更新焦点；超过 4 DIP 才算拖选，避免和单击抢。</summary>
    private void UpdateOcrSelectionDrag(Point point)
    {
        if (_ocrSelAnchor is not { } anchor)
            return;

        if (!_ocrDragging
            && Math.Abs(point.X - _ocrPressPoint.X) + Math.Abs(point.Y - _ocrPressPoint.Y) < 4)
        {
            return;
        }

        _ocrDragging = true;

        var line = OcrLineAt(point);
        if (line < 0)
            return;

        _ocrSelFocus = (line, OcrTextLayout.IndexAt(_ocrLines[line].Chars, point));
        RebuildOcrSelectionVisual();
    }

    private int OcrLineAt(Point point)
    {
        for (var i = 0; i < _ocrLines.Count; i++)
        {
            var line = _ocrLines[i].Line;
            var rect = new Rect(line.X - 4, line.Y - 4, line.Width + 8, line.Height + 8);

            if (rect.Contains(point))
                return i;
        }

        return -1;
    }

    /// <summary>把 anchor/focus 归一成「每行一段」的字符区间。</summary>
    internal IReadOnlyList<(int Line, int Start, int Length)> GetOcrSelectionRanges()
    {
        var result = new List<(int Line, int Start, int Length)>();

        if (_ocrSelAnchor is not { } a || _ocrSelFocus is not { } f)
            return result;

        if (a.Line == f.Line && a.Char == f.Char)
            return result;

        if (a.Line > f.Line || (a.Line == f.Line && a.Char > f.Char))
            (a, f) = (f, a);

        for (var line = a.Line; line <= f.Line; line++)
        {
            if (line < 0 || line >= _ocrLines.Count)
                continue;

            var count = _ocrLines[line].Chars.Count;
            var start = line == a.Line ? a.Char : 0;
            var end = line == f.Line ? f.Char : count;

            start = Math.Clamp(start, 0, count);
            end = Math.Clamp(end, 0, count);

            if (end > start)
                result.Add((line, start, end - start));
        }

        return result;
    }

    /// <summary>选区文字：行内取子串，行间换行。</summary>
    internal string SelectedOcrText()
    {
        var ranges = GetOcrSelectionRanges();
        if (ranges.Count == 0)
            return string.Empty;

        return string.Join("\n", ranges.Select(range =>
            OcrTextLayout.TextForRange(_ocrLines[range.Line].Line.Text, range.Start, range.Length)));
    }

    /// <summary>测试与将来的键盘选择用：直接设定字符区间。</summary>
    internal void SetOcrSelection(int line, int start, int end)
    {
        _ocrSelAnchor = (line, start);
        _ocrSelFocus = (line, end);
        _ocrDragging = true;
        RebuildOcrSelectionVisual();
    }

    /// <summary>已打上的文字标记数量。</summary>
    internal int OcrMarkCount => _ocrMarks.Count;

    /// <summary>把选区的字符框画成半透明底（和行框的选中态区分开）。</summary>
    private void RebuildOcrSelectionVisual()    {
        RemoveOcrChildren("ocr-selection");

        foreach (var (line, start, length) in GetOcrSelectionRanges())
        {
            foreach (var box in OcrTextLayout.BoxesForRange(_ocrLines[line].Chars, start, length))
            {
                var highlight = new Rectangle
                {
                    Width = box.Width,
                    Height = box.Height,
                    Fill = OcrCharSelectionFill,
                    IsHitTestVisible = false,
                    Tag = "ocr-selection"
                };

                Canvas.SetLeft(highlight, box.X);
                Canvas.SetTop(highlight, box.Y);
                OcrLayer.Children.Add(highlight);
            }
        }
    }

    private void ClearOcrCharSelection()
    {
        _ocrSelAnchor = null;
        _ocrSelFocus = null;
        _ocrDragging = false;
        RebuildOcrSelectionVisual();
    }

    /// <summary>当前是否有行选中或字符选中。</summary>
    internal bool HasOcrSelection() =>
        _selectedOcrBoxes.Count > 0 || _ocrSelAnchor is not null || _ocrSelFocus is not null;

    /// <summary>
    /// 点击空白只取消文本选择，保留识别结果：
    /// 复制一部分后还能继续选别的行，不需要重新识别。
    /// </summary>
    internal void ClearOcrSelection()
    {
        if (!HasOcrSelection())
            return;

        _selectedOcrBoxes.Clear();
        ClearOcrCharSelection();

        foreach (var box in _ocrBoxes)
            ApplyOcrBoxVisual(box, hovered: false);

        UpdateOcrSelectionStatus();
    }

    /// <summary>切换一条文字标记：同样类型 + 同样框再点一次就取消。</summary>
    internal void ApplyOcrMark(TextMarkKind kind)
    {
        var ranges = GetOcrSelectionRanges();
        if (ranges.Count == 0)
            return;

        var boxes = ranges
            .SelectMany(range => OcrTextLayout.BoxesForRange(
                _ocrLines[range.Line].Chars, range.Start, range.Length))
            .ToList();

        if (boxes.Count == 0)
            return;

        var existing = _ocrMarks.FindIndex(mark => mark.Kind == kind && SameBoxes(mark.Boxes, boxes));

        if (existing >= 0)
            _ocrMarks.RemoveAt(existing);
        else
            _ocrMarks.Add(new OcrTextMark(kind, boxes, OcrTextMarks.ColorFor(kind), 0));

        RebuildOcrMarksVisual();
    }

    private static bool SameBoxes(IReadOnlyList<Rect> left, IReadOnlyList<Rect> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (Math.Abs(left[i].X - right[i].X) > 0.5 ||
                Math.Abs(left[i].Y - right[i].Y) > 0.5 ||
                Math.Abs(left[i].Width - right[i].Width) > 0.5 ||
                Math.Abs(left[i].Height - right[i].Height) > 0.5)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把标记画到 OCR 层（预览）；导出时同一批标记会画进 PNG。</summary>
    private void RebuildOcrMarksVisual()
    {
        RemoveOcrChildren("ocr-mark");

        foreach (var mark in _ocrMarks)
        {
            var brush = new SolidColorBrush(Color.FromUInt32(mark.Color));

            foreach (var box in mark.Boxes)
            {
                if (mark.Kind == TextMarkKind.Highlight)
                {
                    var fill = new Rectangle
                    {
                        Width = box.Width,
                        Height = box.Height,
                        Fill = brush,
                        IsHitTestVisible = false,
                        Tag = "ocr-mark"
                    };

                    Canvas.SetLeft(fill, box.X);
                    Canvas.SetTop(fill, box.Y);
                    OcrLayer.Children.Add(fill);
                    continue;
                }

                var width = Math.Clamp(box.Height / 14, 1.5, 3);
                var y = mark.Kind == TextMarkKind.Strike ? box.Center.Y : box.Bottom - 1;
                var line = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = width,
                    IsHitTestVisible = false,
                    Tag = "ocr-mark"
                };

                if (mark.Kind == TextMarkKind.Wave)
                {
                    for (var x = box.X; x <= box.Right; x += 1)
                        line.Points.Add(new Point(x, y + Math.Sin((x - box.X) * 0.7) * width));
                }
                else
                {
                    line.Points.Add(new Point(box.X, y));
                    line.Points.Add(new Point(box.Right, y));
                }

                OcrLayer.Children.Add(line);
            }
        }
    }

    private void RemoveOcrChildren(string tag)
    {
        for (var i = OcrLayer.Children.Count - 1; i >= 0; i--)
        {
            if (OcrLayer.Children[i] is Control { Tag: string value } && value == tag)
                OcrLayer.Children.RemoveAt(i);
        }
    }

    /// <summary>在选区上方/下方摆浮动菜单。</summary>
    private void ShowOcrMarkMenu()
    {
        var ranges = GetOcrSelectionRanges();
        if (ranges.Count == 0)
        {
            HideOcrMarkMenu();
            return;
        }

        var boxes = ranges
            .SelectMany(range => OcrTextLayout.BoxesForRange(
                _ocrLines[range.Line].Chars, range.Start, range.Length))
            .ToList();

        var bounds = boxes.Skip(1).Aggregate(boxes[0], (current, box) => current.Union(box));

        OcrMarkMenu.IsVisible = true;
        OcrMarkMenu.Measure(Size.Infinity);

        var size = OcrMarkMenu.DesiredSize;
        var origin = OcrLayer.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        var left = origin.X + bounds.Center.X - size.Width / 2;
        var top = origin.Y + bounds.Bottom + 10;

        if (top + size.Height > Height - 8)
            top = origin.Y + bounds.Top - size.Height - 10;

        Canvas.SetLeft(OcrMarkMenu, Math.Clamp(left, 8, Math.Max(8, Width - size.Width - 8)));
        Canvas.SetTop(OcrMarkMenu, Math.Max(8, top));
    }

    private void HideOcrMarkMenu() => OcrMarkMenu.IsVisible = false;

    private void OnOcrMarkCopyClick(object? sender, RoutedEventArgs e)
    {
        var text = SelectedOcrText();
        if (string.IsNullOrEmpty(text))
            return;

        CopyText?.Invoke(text);
        OcrStatus.Text = $"已复制选中文字 · {text.Length} 字";
    }

    private void OnOcrHighlightClick(object? sender, RoutedEventArgs e) => ApplyOcrMark(TextMarkKind.Highlight);

    private void OnOcrWaveClick(object? sender, RoutedEventArgs e) => ApplyOcrMark(TextMarkKind.Wave);

    private void OnOcrUnderlineClick(object? sender, RoutedEventArgs e) => ApplyOcrMark(TextMarkKind.Underline);

    private void OnOcrStrikeClick(object? sender, RoutedEventArgs e) => ApplyOcrMark(TextMarkKind.Strike);

    /// <summary>切换一行框的选中状态（点击和测试共用）。</summary>
    internal void ToggleOcrBoxSelection(Rectangle box)
    {
        if (!_selectedOcrBoxes.Remove(box))
            _selectedOcrBoxes.Add(box);

        // 点完先按「未悬停」画：悬停态交给 PointerEntered，避免和选中态撞色
        ApplyOcrBoxVisual(box, hovered: false);
        UpdateOcrSelectionStatus();
    }

    /// <summary>按「是否选中 / 是否悬停」刷新一个行框的三态外观。</summary>
    private void ApplyOcrBoxVisual(Rectangle box, bool hovered)
    {
        if (_selectedOcrBoxes.Contains(box))
        {
            // 选中：琥珀实线 + 淡琥珀底，和工具条选中态一致
            box.Stroke = LineHoverStroke;
            box.StrokeThickness = 2;
            box.Fill = LineSelectedFill;
            return;
        }

        // 常态红框 1px；悬停红框 2px + 更重的底
        box.Stroke = LineStroke;
        box.StrokeThickness = hovered ? 2 : 1;
        box.Fill = hovered ? LineHoverFill : LineFill;
    }

    /// <summary>选中数变化后刷新状态文字和复制按钮的提示。</summary>
    private void UpdateOcrSelectionStatus()
    {
        UpdateOcrStatus();

        var count = _selectedOcrBoxes.Count;
        ToolTip.SetTip(
            CopyImageButton,
            count > 0 ? $"复制选中的 {count} 行（Ctrl+C）" : "复制识别文字（Ctrl+C）");
    }

    private static string Shorten(string text) =>
        text.Length <= 18 ? text : text[..18] + "…";

    /// <summary>
    /// 主条唯一的复制入口：识别后复制当前可见文本（选区 / 译文 / 全文），
    /// 否则完成并复制图片——不需要用户判断该点哪个复制。
    /// </summary>
    private async void OnCopyImageClick(object? sender, RoutedEventArgs e)
    {
        if (_ocrActionsAvailable)
        {
            CopyOcrText();
            return;
        }

        await ActAsync(CaptureAction.CopyImage);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.Save);

    private async void OnPinClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.Pin);

    private async void OnScrollClick(object? sender, RoutedEventArgs e) => await ActAsync(CaptureAction.Scroll);

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Action = CaptureAction.None;
        Close();
    }
}
