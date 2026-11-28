// WorkspaceViewModel 分部类：工具执行与各类结果展示。
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.History;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.ViewModels;

public partial class WorkspaceViewModel
{
    public async Task ExecuteAsync()
    {
        if (IsExecuting || SelectedTool is null)
            return;

        var entry = SelectedTool.Entry;

        if (!entry.IsAvailable)
        {
            ShowError(entry.UnavailableReason ?? "当前环境不支持这个工具");
            return;
        }

        var input = BuildInput(entry.Descriptor, out var inputError);
        if (input is null)
        {
            ShowError(inputError ?? "无法构造输入");
            return;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
            parameters[parameter.Key] = parameter.Value;

        // 执行即记忆：下次打开这个工具时参数保持上次的值
        _parameters?.Save(entry.Descriptor.Id, parameters);

        // 每次执行独立的取消源：面板 Esc / 工作区「取消」按钮都走它
        using var cancellation = new CancellationTokenSource();
        _executionCts = cancellation;

        IsExecuting = true;
        ProgressText = null;

        // 只让本次执行的进度更新界面：执行结束后迟到的回调不能污染下一次
        var generation = ++_progressGeneration;
        var progress = new Progress<ToolProgress>(update =>
        {
            if (generation != _progressGeneration)
                return;

            ProgressText = update switch
            {
                { Message: { Length: > 0 } message, Percent: { } percent }
                    => $"{message}（{percent * 100:F0}%）",
                { Message: { Length: > 0 } message } => message,
                { Percent: { } percent } => $"执行中… {percent * 100:F0}%",
                _ => null
            };
        });

        try
        {
            var context = new ToolContext
            {
                Input = input,
                Source = input is FileSetInput or ImageSetInput
                    ? ToolSource.Dropped
                    : ToolSource.Clipboard,
                Parameters = parameters,
                Host = entry.Host,
                Progress = progress,
                Files = input is FileSetInput files ? new ScopedFileAccess(files.Paths) : null
            };

            var startedAt = Stopwatch.GetTimestamp();
            var result = await ToolRunner.ExecuteAsync(entry.Tool, context, cancellation.Token);
            var elapsed = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            if (result.Success && result.Payload is not null)
            {
                switch (result.Payload)
                {
                    case SvgPayload svgPayload:
                        await ShowSvgResultAsync(entry, context, svgPayload, elapsed);
                        return;

                    case OcrResultPayload ocrPayload when ocrPayload.Sections is { Count: > 0 } sections:
                        // 单张也走这里：右侧仍是纯文本（HasSections 会是 false），
                        // 但 ResultSections 要有数据——左侧的识别框靠 CurrentSection 拿行框
                        ShowSectionResult(entry, context, ocrPayload, sections, elapsed);
                        return;

                    case TextPayload or OcrResultPayload:
                        var text = result.Payload switch
                        {
                            TextPayload textPayload => textPayload.Text,
                            OcrResultPayload ocrPayload => ocrPayload.Text,
                            _ => string.Empty
                        };

                        ShowTextResult(entry, context, text, elapsed);
                        return;

                    case ImagePayload imagePayload:
                        ShowImageResult(entry, context, imagePayload, elapsed);
                        return;

                    case TablePayload tablePayload:
                        ShowTableResult(entry, context, tablePayload, elapsed);
                        return;

                    case FileSetPayload fileSetPayload:
                        ShowFileSetResult(entry, context, fileSetPayload, elapsed);
                        return;

                    default:
                        // 新载荷类型不该被当成失败：成功就是成功，只是宿主还没做专用渲染
                        ShowTextResult(
                            entry,
                            context,
                            $"（{result.Payload.GetType().Name} 结果，暂不支持展示）",
                            elapsed);
                        return;
                }
            }

            ShowError(result.Error?.Message ?? "执行失败");
        }
        catch (Exception ex)
        {
            // 插件异常不能带走工作区
            _log.Error("工作区执行失败", ex);
            ShowError($"工具执行异常：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_executionCts, cancellation))
                _executionCts = null;

            _progressGeneration++;
            ProgressText = null;
            IsExecuting = false;
        }
    }

    /// <summary>取消当前执行；没有执行时是空操作。</summary>
    public void CancelExecution() => _executionCts?.Cancel();

    private void ShowTextResult(ToolEntry entry, ToolContext context, string text, long elapsed)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ClearStructuredResults();

        // 单段结果或普通文本：清掉上一次的分段列表，避免列表残留
        ResultSections.Clear();
        HasSections = false;

        ResultText = text;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, text, elapsed);
    }

    /// <summary>表格结果：宿主用只读表格渲染；纯文本版本仍留给复制和历史。</summary>
    private void ShowTableResult(ToolEntry entry, ToolContext context, TablePayload payload, long elapsed)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ResultSections.Clear();
        HasSections = false;
        ClearStructuredResults();

        var widths = BuildColumnWidths(payload);

        for (var index = 0; index < payload.Columns.Count; index++)
            ResultTableColumns.Add(new ResultTableColumnViewModel(payload.Columns[index], widths[index]));

        foreach (var row in payload.Rows)
        {
            var cells = new List<ResultTableCellViewModel>(widths.Length);
            for (var index = 0; index < widths.Length; index++)
                cells.Add(new ResultTableCellViewModel(Cell(row, index), widths[index]));

            ResultTableRows.Add(new ResultTableRowViewModel(cells));
        }

        HasTableResult = true;

        var text = ResultTextFormatter.Format(payload);
        ResultText = text;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, text, elapsed);
    }

    /// <summary>文件集结果：宿主用文件卡渲染；纯文本版本仍留给复制和历史。</summary>
    private void ShowFileSetResult(ToolEntry entry, ToolContext context, FileSetPayload payload, long elapsed)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ResultSections.Clear();
        HasSections = false;
        ClearStructuredResults();

        foreach (var path in payload.Paths)
            ResultFiles.Add(new ResultFileViewModel(path));

        HasFileSetResult = true;

        var text = ResultTextFormatter.Format(payload);
        ResultText = text;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, text, elapsed);
    }

    private void ClearStructuredResults()
    {
        ResultTableColumns.Clear();
        ResultTableRows.Clear();
        ResultFiles.Clear();
        HasTableResult = false;
        HasFileSetResult = false;
    }

    /// <summary>列宽按内容估算；等宽字体 12px，CJK 按两列算。上限防止超长哈希把表格撑爆。</summary>
    private static double[] BuildColumnWidths(TablePayload table)
    {
        var widths = new int[table.Columns.Count];

        for (var index = 0; index < table.Columns.Count; index++)
            widths[index] = ResultTextFormatter.DisplayWidth(table.Columns[index]);

        foreach (var row in table.Rows)
        {
            for (var index = 0; index < widths.Length; index++)
                widths[index] = Math.Max(widths[index], ResultTextFormatter.DisplayWidth(Cell(row, index)));
        }

        return widths
            .Select(display => Math.Clamp(display * 7.0 + 28, 120, 360))
            .ToArray();
    }

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index < row.Count ? row[index] ?? string.Empty : string.Empty;

    /// <summary>
    /// 多图识别的分段结果。列表负责「哪张图 / 哪段文字 / 哪张失败」，
    /// 纯文本仍留在 <see cref="ResultText"/> 里——复制全部和历史用的就是它。
    /// </summary>
    private void ShowSectionResult(
        ToolEntry entry,
        ToolContext context,
        OcrResultPayload payload,
        IReadOnlyList<TextSection> sections,
        long elapsed)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ClearStructuredResults();

        ResultSections.Clear();
        foreach (var section in sections)
        {
            // 按 SourceIndex 找回输入里的原图；找不到就只显示文字，
            // 插件之间不传图片字节，避免同一张图在内存里存两份
            var source = section.SourceIndex >= 0 && section.SourceIndex < InputImages.Count
                ? InputImages[section.SourceIndex]
                : null;

            ResultSections.Add(new TextSectionViewModel(section, source));
        }

        // 只有多段才渲染列表；单张仍显示纯文本，但段数据保留给左侧的识别框
        HasSections = sections.Count > 1;

        // HasSections 从 false 到 false 时不会触发变更通知（单张场景），
        // 但 ResultSections 内容已经换了，依赖它的派生属性要手动通知一次
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(VisibleSections));
        OnPropertyChanged(nameof(CanSwitchSectionView));

        // 新结果出来时回到「只看当前这张」，用户刚识别完通常要逐张核对
        ShowAllSections = false;
        CurrentImageIndex = Math.Clamp(CurrentImageIndex, 0, Math.Max(0, InputImages.Count - 1));

        ResultText = payload.Text;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, payload.Text, elapsed);
    }

    /// <summary>
    /// 图片结果：预览位图，历史只记摘要（不把字节灌进 JSONL）。
    /// 点「复制」回到剪贴板，点「保存」由视图层弹文件对话框落盘。
    /// </summary>
    private void ShowImageResult(ToolEntry entry, ToolContext context, ImagePayload payload, long elapsed)
    {
        ClearSvgResult();
        ClearStructuredResults();
        ResultSections.Clear();
        HasSections = false;

        ImageResultPng = payload.Data.ToArray();
        ImageResultFormat = payload.Format;
        HasImageResult = true;

        var summary = SummarizeImage(payload);
        ResultText = summary;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        AddHistory(entry, context, summary, elapsed);
    }

    private async Task ShowSvgResultAsync(
        ToolEntry entry,
        ToolContext context,
        SvgPayload payload,
        long elapsed)
    {
        var sanitized = SvgSanitizer.Sanitize(payload.Svg);
        if (!sanitized.Success)
        {
            ShowError(sanitized.Error ?? "SVG 无法预览");
            return;
        }

        var svg = sanitized.Svg!;

        ClearStructuredResults();
        HasImageResult = false;
        ImageResultPng = null;
        ResultSections.Clear();
        HasSections = false;

        ResultText = svg;
        SvgSource = svg;
        SvgPreviewPng = null;
        SvgPreviewError = null;
        ShowSvgSource = false;
        HasResult = true;
        HasError = false;
        ErrorText = null;

        SvgPreviewPng = await _svgPreview.RenderAsync(svg, CancellationToken.None);

        if (SvgPreviewPng is null)
        {
            SvgPreviewError = "预览渲染失败，已显示源码";
            ShowSvgSource = true;
        }

        AddHistory(entry, context, svg, elapsed);
    }

    private void AddHistory(ToolEntry entry, ToolContext context, string output, long elapsed)
    {
        var historyEntry = new HistoryEntry(
            Guid.NewGuid().ToString("N"),
            entry.Descriptor.Id,
            entry.Descriptor.Name,
            output,
            PreviewInput(context.Input),
            DateTimeOffset.Now,
            elapsed);

        _history.Add(historyEntry);
        _allHistory.Insert(0, new HistoryEntryViewModel(historyEntry));
        RebuildRecentTools();
    }

    /// <summary>右上角复制的说明：多图时明确写出「全部 N 张」，避免和每列的单条复制混淆。</summary>
    public string CopyAllHint => HasSections
        ? $"复制全部 {ResultSections.Count} 张的识别结果"
        : "复制结果到剪贴板";
}
