// WorkspaceViewModel 分部类：工具导航、分类、最近使用与输入图片选择。
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
    /// <summary>打开一个工具：进入工具台。</summary>
    public void OpenTool(ToolItemViewModel tool)
    {
        SelectedTool = tool;
        IsDetailOpen = true;
        IsClipboardOpen = false;
    }

    /// <summary>返回工具墙。</summary>
    public void CloseDetail()
    {
        PersistCurrentParameters();
        IsDetailOpen = false;
    }

    /// <summary>侧栏点分类；name 为 null 表示「全部」。</summary>
    public void SelectCategory(string? name)
    {
        IsClipboardOpen = false;
        SelectedCategory = name;
    }

    /// <summary>卡片右键：把工具固定到系统托盘，或从托盘移除。</summary>
    public void ToggleToolTrayPin(ToolItemViewModel tool)
    {
        var id = tool.Entry.Descriptor.Id;
        var pinned = !Settings.IsToolPinnedToTray(id);

        Settings.SetToolPinnedToTray(id, pinned);
        tool.IsPinnedToTray = pinned;
    }

    /// <summary>切到剪贴板页；每次进入都重新读一遍，后台新复制的条目立刻能看到。</summary>
    public void ShowClipboard()
    {
        PersistCurrentParameters();
        IsDetailOpen = false;
        IsClipboardOpen = true;
        Clipboard?.Refresh();
    }

    /// <summary>侧栏回到工具墙。</summary>
    public void ShowTools()
    {
        PersistCurrentParameters();
        IsDetailOpen = false;
        IsClipboardOpen = false;
    }

    /// <summary>
    /// 离开工具台前记住参数：默认参数区和自定义视图写入的未声明参数都要落盘，
    /// 否则「改完直接返回」下一次打开会丢。
    /// </summary>
    private void PersistCurrentParameters()
    {
        if (IsDetailOpen && SelectedTool is { } tool)
            PersistParameters(tool);
    }

    public bool HasRecentTools => RecentTools.Count > 0;

    /// <summary>输入框提示：图片集 / 拖入的图片 / 文件清单 / 工具输入形态，依次给出说明。</summary>
    public string InputHint => IsImageInput && HasDroppedImage
        ? $"将使用拖入的图片：{_droppedImageName}"
        : IsImagesInput
            ? InputImageSummary
            : IsFilesInput
                ? DroppedFileSummary
                : SelectedTool?.Entry.Descriptor.Accepts switch
                {
                    InputShape.Image => "将使用剪贴板里的图片",
                    InputShape.None => "此工具不需要输入",
                    _ => "输入内容…"
                };

    /// <summary>图片集提示：张数 + 前三个文件名，其余折叠。</summary>
    private string InputImageSummary
    {
        get
        {
            if (InputImages.Count == 0)
                return "把图片拖进窗口，或点「添加图片…」（可多选）";

            var names = InputImages
                .Take(3)
                .Select(image => image.Name)
                .Where(name => name.Length > 0);

            var text = string.Join("、", names);
            return InputImages.Count > 3
                ? $"将识别 {InputImages.Count} 张图片：{text} 等"
                : $"将识别 {InputImages.Count} 张图片：{text}";
        }
    }

    /// <summary>文件输入工具的提示：列出前几个文件名，其余折叠成「等 N 个」。</summary>
    private string DroppedFileSummary
    {
        get
        {
            if (_droppedFiles.Count == 0)
                return "把文件拖进窗口，或点「选择文件…」（可多选）";

            var names = _droppedFiles
                .Select(Path.GetFileName)
                .Take(3)
                .Select(name => name ?? string.Empty);

            var text = string.Join("、", names);
            return _droppedFiles.Count > 3
                ? $"将处理 {_droppedFiles.Count} 个文件：{text} 等"
                : $"将处理 {_droppedFiles.Count} 个文件：{text}";
        }
    }

    public bool IsTextInput => SelectedTool?.Entry.Descriptor.Accepts is InputShape.Text or InputShape.RichText;

    public bool IsImageInput => SelectedTool?.Entry.Descriptor.Accepts == InputShape.Image;

    /// <summary>文件类工具（批量识别等）：输入来自拖入或选择的多个文件。</summary>
    public bool IsFilesInput => SelectedTool?.Entry.Descriptor.Accepts == InputShape.Files;

    /// <summary>图片集工具（OCR）：输入是 1..N 张图片，来源不限。</summary>
    public bool IsImagesInput => SelectedTool?.Entry.Descriptor.Accepts == InputShape.Images;

    /// <summary>图片集里的图片；缩略图条直接绑定它。</summary>
    public ObservableCollection<InputImageViewModel> InputImages { get; } = [];

    public int InputImageCount => InputImages.Count;

    /// <summary>当前轮播到第几张（从 0 起）。</summary>
    [ObservableProperty]
    private int _currentImageIndex;

    public bool HasMultipleImages => InputImages.Count > 1;

    public bool CanGoPreviousImage => CurrentImageIndex > 0;

    public bool CanGoNextImage => CurrentImageIndex < InputImages.Count - 1;

    /// <summary>当前显示的那张原图。</summary>
    public InputImageViewModel? CurrentImage =>
        CurrentImageIndex >= 0 && CurrentImageIndex < InputImages.Count
            ? InputImages[CurrentImageIndex]
            : null;

    public string ImageCounter => InputImages.Count == 0
        ? string.Empty
        : $"{CurrentImageIndex + 1} / {InputImages.Count}";

    public void PreviousImage()
    {
        if (CanGoPreviousImage)
            CurrentImageIndex--;
    }

    public void NextImage()
    {
        if (CanGoNextImage)
            CurrentImageIndex++;
    }

    /// <summary>点小缩略图直接跳到某张。</summary>
    public void SelectImage(InputImageViewModel image)
    {
        var index = InputImages.IndexOf(image);
        if (index >= 0)
            CurrentImageIndex = index;
    }

    partial void OnCurrentImageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoPreviousImage));
        OnPropertyChanged(nameof(CanGoNextImage));
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(ImageCounter));
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(VisibleSections));
    }

    /// <summary>
    /// 多图结果：false = 只看当前这张（默认，方便逐张核对）；
    /// true = 看全部（总览 / 复制全部）。
    /// </summary>
    [ObservableProperty]
    private bool _showAllSections;

    public bool IsCurrentSectionView => !ShowAllSections;

    /// <summary>只有一段时两个视图内容一样，切换按钮不该出现。</summary>
    public bool CanSwitchSectionView => ResultSections.Count > 1;

    /// <summary>当前轮播图对应的那段识别结果。</summary>
    public TextSectionViewModel? CurrentSection =>
        ResultSections.FirstOrDefault(section => section.SourceIndex == CurrentImageIndex)
        ?? ResultSections.FirstOrDefault();

    partial void OnShowAllSectionsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCurrentSectionView));
        OnPropertyChanged(nameof(VisibleSections));
    }

    /// <summary>
    /// 结果区实际渲染的段：只看当前时是一个元素，看全部时是整份列表。
    /// 两个视图共用同一批 TextSectionViewModel，不复制数据。
    /// </summary>
    public IReadOnlyList<TextSectionViewModel> VisibleSections =>
        ShowAllSections
            ? ResultSections
            : CurrentSection is { } current
                ? [current]
                : [];

    public bool HasInputImages => InputImages.Count > 0;

    public bool CanClearInputImages => IsImagesInput && HasInputImages;

    /// <summary>页内大图预览（点缩略图打开）。</summary>
    [ObservableProperty]
    private bool _isImagePreviewVisible;

    [ObservableProperty]
    private byte[]? _previewImagePng;

    public int DroppedFileCount => _droppedFiles.Count;

    /// <summary>只有当前是图片工具且确实拖入了图片，才显示「清除」。</summary>
    public bool CanClearDroppedImage => HasDroppedImage && IsImageInput;

    /// <summary>文件工具的「清除」：清空本次选择的文件清单。</summary>
    public bool CanClearDroppedFiles => IsFilesInput && _droppedFiles.Count > 0;
}
