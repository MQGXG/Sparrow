// WorkspaceViewModel 分部类：复制结果/历史、设置显示、错误与结果清理。
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
    /// <summary>点某一段时把左侧轮播切到它对应的图 —— 眼睛和框都跟着走。</summary>
    public void SelectSectionImage(TextSectionViewModel section)
    {
        if (section.SourceIndex >= 0 && section.SourceIndex < InputImages.Count)
            CurrentImageIndex = section.SourceIndex;
    }

    /// <summary>复制一段纯文字（点识别框复制该行用）。</summary>
    public void CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (_clipboard.SetText(text))
            _log.Info($"已复制一行：{text.Length} 字");
    }

    /// <summary>复制某一段的识别结果（含译文，如果选了翻译）。</summary>
    public void CopySectionText(TextSectionViewModel section)
    {
        if (string.IsNullOrWhiteSpace(section.Text))
        {
            _log.Warn("这一条没有可复制的文字");
            return;
        }

        if (_clipboard.SetText(section.Text))
            _log.Info($"已复制「{section.Title}」：{section.Text.Length} 字");
    }

    public void CopyResult()
    {
        // 图片结果复制回剪贴板（JPEG/WebP 先转 PNG，写剪贴板只认 PNG）
        if (HasImageResult && ImageResultPng is { Length: > 0 } bytes)
        {
            var png = ImageResultFormat == ImageFormat.Png ? bytes : ImageBytes.ToPng(bytes);
            if (png is null)
            {
                _log.Warn("图片结果转 PNG 失败，无法复制");
                return;
            }

            if (_clipboard.SetImagePng(png))
                _log.Info($"图片结果已复制：{png.Length} 字节");
            else
                _log.Warn("图片结果写入剪贴板失败");

            return;
        }

        if (!string.IsNullOrEmpty(ResultText))
            _clipboard.SetText(ResultText);
    }

    /// <summary>点击历史记录：把内容放回结果区和剪贴板。</summary>
    public void CopyHistory(HistoryEntryViewModel history)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ClearStructuredResults();

        ResultText = history.Entry.Output;
        HasResult = true;
        HasError = false;
        ErrorText = null;
        _clipboard.SetText(history.Entry.Output);
    }

    public void ClearHistory()
    {
        _history.Clear();
        _allHistory.Clear();
        RecentTools.Clear();
        OnPropertyChanged(nameof(HasRecentTools));
    }

    public void ShowSettings() => IsSettingsVisible = true;

    public void HideSettings() => IsSettingsVisible = false;

    private void ShowError(string message)
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ClearStructuredResults();
        ErrorText = message;
        HasError = true;
        HasResult = false;
        ResultText = null;
    }

    private void ClearResult()
    {
        ClearSvgResult();
        HasImageResult = false;
        ImageResultPng = null;
        ClearStructuredResults();
        ResultText = null;
        ErrorText = null;
        HasResult = false;
        HasError = false;

        ResultSections.Clear();
        HasSections = false;
        ShowAllSections = false;
    }

    private void ClearSvgResult()
    {
        SvgSource = null;
        SvgPreviewPng = null;
        SvgPreviewError = null;
        ShowSvgSource = false;
    }
}
