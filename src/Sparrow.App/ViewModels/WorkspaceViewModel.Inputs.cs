// WorkspaceViewModel 分部类：输入图片与文件的添加、移除与预览。
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
    /// <summary>
    /// 处理拖入的文件：图片进图片集 / 单图输入，多文件进文件清单，
    /// 文本文件填输入框。分流规则见 docs/plans/2026-11-18-ocr-image-set-design.md。
    /// </summary>
    public void AcceptDroppedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        var imagePaths = paths
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .ToList();

        // 全是图片：交给图片集（OCR 这类工具），不再要求用户先选对卡片
        if (imagePaths.Count == paths.Count)
        {
            AddInputImages(paths);

            if (InputImages.Count == 0)
                return;

            // 当前是单图工具：保留「拖入图片优先」的老行为
            if (IsImageInput)
                SetDroppedImage(imagePaths[0]);
            else if (!IsImagesInput)
                OpenFirstMatching(InputShape.Images);

            return;
        }

        SetDroppedFiles(paths);

        // 当前就在图片集工具里：混合文件也只收图片，不跳走
        if (IsImagesInput)
        {
            if (imagePaths.Count > 0)
                AddInputImages(imagePaths);

            return;
        }

        // 当前就是文件类工具：多选文件全部留给它，不做单文件特化
        if (IsFilesInput)
            return;

        // 混合 / 多选：有文件类工具就直接切过去
        if (paths.Count > 1 && OpenFirstMatching(InputShape.Files))
            return;

        var textPath = paths.FirstOrDefault(path => TextExtensions.Contains(Path.GetExtension(path)));
        if (textPath is not null)
        {
            try
            {
                var info = new FileInfo(textPath);
                if (info.Length > MaxDroppedTextBytes)
                {
                    ShowError("文本文件超过 2 MB，请先拆分");
                    return;
                }

                InputText = File.ReadAllText(textPath);

                if (!IsTextInput)
                    OpenFirstMatching(InputShape.Text);

                return;
            }
            catch (Exception ex)
            {
                ShowError($"读取文本失败：{ex.Message}");
                return;
            }
        }

        ShowError("只支持拖入图片或文本文件");
    }

    /// <summary>把一张图片设为单图工具的输入（拖入优先于剪贴板）。</summary>
    private void SetDroppedImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxDroppedImageBytes)
            {
                ShowError("图片超过 20 MB，请先压缩");
                return;
            }

            _droppedImagePng = File.ReadAllBytes(path);
            _droppedImageFormat = FormatFromExtension(Path.GetExtension(path));
            _droppedImageName = Path.GetFileName(path);
            HasDroppedImage = true;
        }
        catch (Exception ex)
        {
            ShowError($"读取图片失败：{ex.Message}");
        }
    }

    /// <summary>「添加图片…」的结果：加入图片集，必要时切到图片集工具。</summary>
    public void AcceptPickedImages(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        AddInputImages(paths);

        if (InputImages.Count > 0 && !IsImagesInput)
            OpenFirstMatching(InputShape.Images);
    }

    /// <summary>
    /// 把磁盘图片加入图片集；返回实际加入数量。
    /// 非图片、超限、重复路径都会跳过并说明，不让一次坏文件毁掉整次选择。
    /// </summary>
    public int AddInputImages(IReadOnlyList<string> paths)
    {
        var added = 0;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(path)))
                continue;

            if (InputImages.Count >= MaxInputImageCount)
            {
                ShowError($"最多一次处理 {MaxInputImageCount} 张图片");
                break;
            }

            if (InputImages.Any(image =>
                    string.Equals(image.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var info = new FileInfo(path);

                if (info.Length > MaxDroppedImageBytes)
                {
                    ShowError($"「{info.Name}」超过 20 MB，已跳过");
                    continue;
                }

                var total = InputImages.Sum(image => (long)image.Input.ByteSize) + info.Length;
                if (total > MaxInputImagesTotalBytes)
                {
                    ShowError("图片总大小超过 200 MB，已停止添加");
                    break;
                }

                var bytes = File.ReadAllBytes(path);
                var (width, height) = ImageSizeReader.Read(bytes);
                var input = new ImageInput(bytes, FormatFromExtension(Path.GetExtension(path)))
                {
                    Name = info.Name,
                    Width = width,
                    Height = height
                };

                InputImages.Add(new InputImageViewModel(input, path));
                added++;
            }
            catch (Exception ex)
            {
                ShowError($"读取图片失败：{ex.Message}");
            }
        }

        NotifyInputImagesChanged();
        return added;
    }

    /// <summary>把剪贴板图片加入图片集。</summary>
    public void AddClipboardImage()
    {
        var png = _clipboard.GetImagePng();
        if (png is null)
        {
            ShowError("剪贴板里没有图片");
            return;
        }

        if (InputImages.Count >= MaxInputImageCount)
        {
            ShowError($"最多一次处理 {MaxInputImageCount} 张图片");
            return;
        }

        var name = InputImages.Count == 0
            ? "剪贴板图片"
            : $"剪贴板图片 {InputImages.Count + 1}";

        var (imageWidth, imageHeight) = ImageSizeReader.Read(png);
        InputImages.Add(new InputImageViewModel(
            new ImageInput(png, ImageFormat.Png)
            {
                Name = name,
                Width = imageWidth,
                Height = imageHeight
            },
            sourcePath: null));

        NotifyInputImagesChanged();

        if (!IsImagesInput)
            OpenFirstMatching(InputShape.Images);
    }

    public void RemoveInputImage(InputImageViewModel image)
    {
        var index = InputImages.IndexOf(image);
        if (index < 0)
            return;

        InputImages.RemoveAt(index);

        if (IsImagePreviewVisible)
            HideImagePreview();

        RemoveSectionFor(index);
        NotifyInputImagesChanged();
    }

    /// <summary>
    /// 删掉图片后，它对应的那段识别结果也要消失，后面段的下标整体前移——
    /// 不然 SourceIndex 和 CurrentImageIndex 会错位，切图时右侧指向错误的文字。
    /// </summary>
    private void RemoveSectionFor(int imageIndex)
    {
        var section = ResultSections.FirstOrDefault(item => item.SourceIndex == imageIndex);
        if (section is not null)
            ResultSections.Remove(section);

        foreach (var rest in ResultSections)
        {
            if (rest.SourceIndex > imageIndex)
                rest.SourceIndex--;
        }

        HasSections = ResultSections.Count > 0;
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(VisibleSections));
        OnPropertyChanged(nameof(CanSwitchSectionView));
    }

    public void ClearInputImages()
    {
        if (InputImages.Count == 0)
            return;

        InputImages.Clear();

        // 输入全清掉，结果也没有对应关系了，一并收起
        ResultSections.Clear();
        HasSections = false;
        ShowAllSections = false;

        HideImagePreview();
        NotifyInputImagesChanged();
    }

    public void ShowImagePreview(InputImageViewModel image)
    {
        PreviewImagePng = image.PreviewBytes;
        IsImagePreviewVisible = true;
    }

    public void HideImagePreview()
    {
        IsImagePreviewVisible = false;
        PreviewImagePng = null;
    }

    private void NotifyInputImagesChanged()
    {
        OnPropertyChanged(nameof(InputImageCount));
        OnPropertyChanged(nameof(HasInputImages));
        OnPropertyChanged(nameof(CanClearInputImages));
        OnPropertyChanged(nameof(InputHint));

        // 轮播相关：数量变了，索引可能要夹回合法范围
        OnPropertyChanged(nameof(HasMultipleImages));
        OnPropertyChanged(nameof(CurrentImage));
        OnPropertyChanged(nameof(ImageCounter));
        OnPropertyChanged(nameof(CanGoPreviousImage));
        OnPropertyChanged(nameof(CanGoNextImage));
        OnPropertyChanged(nameof(CurrentSection));

        if (CurrentImageIndex >= InputImages.Count)
            CurrentImageIndex = Math.Max(0, InputImages.Count - 1);
    }

    public void ClearDroppedImage()
    {
        _droppedImagePng = null;
        _droppedImageName = null;
        HasDroppedImage = false;
    }

    /// <summary>「选择文件…」的结果：和拖入走同一条记录路径。</summary>
    public void AcceptPickedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        SetDroppedFiles(paths);

        // 从文件工具的选择按钮进来时已经在对的地方；不是则切过去
        if (!IsFilesInput)
            OpenFirstMatching(InputShape.Files);
    }

    public void ClearDroppedFiles()
    {
        if (_droppedFiles.Count == 0)
            return;

        _droppedFiles.Clear();
        NotifyFilesChanged();
    }

    /// <summary>整体替换文件清单；去重并保留用户的选择顺序。</summary>
    private void SetDroppedFiles(IEnumerable<string> paths)
    {
        _droppedFiles.Clear();
        _droppedFiles.AddRange(paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        NotifyFilesChanged();
    }

    private void NotifyFilesChanged()
    {
        OnPropertyChanged(nameof(DroppedFileCount));
        OnPropertyChanged(nameof(InputHint));
        OnPropertyChanged(nameof(CanClearDroppedFiles));
    }

    private bool OpenFirstMatching(InputShape shape)
    {
        var entry = _allTools.FirstOrDefault(tool => tool.Descriptor.Accepts == shape);
        if (entry is null)
            return false;

        OpenTool(new ToolItemViewModel(entry));
        return true;
    }

    private static ImageFormat FormatFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.Webp,
        ".bmp" => ImageFormat.Bmp,
        ".gif" => ImageFormat.Gif,
        _ => ImageFormat.Png
    };

    private IToolInput? BuildInput(ToolDescriptor descriptor, out string? error)
    {
        error = null;

        if (descriptor.Accepts == InputShape.None)
            return EmptyInput.Instance;

        if (descriptor.Accepts == InputShape.Files)
        {
            if (_droppedFiles.Count == 0)
            {
                error = "还没有文件。把文件拖进窗口，或点「选择文件…」。";
                return null;
            }

            return new FileSetInput(_droppedFiles.ToArray());
        }

        if (descriptor.Accepts == InputShape.Images)
        {
            if (InputImages.Count == 0)
            {
                error = "还没有图片。把图片拖进窗口，或点「添加图片…」。";
                return null;
            }

            return new ImageSetInput(InputImages.Select(image => image.Input).ToArray());
        }

        if (descriptor.Accepts == InputShape.Image)
        {
            // 拖入的图片优先；没有才回退到剪贴板
            if (_droppedImagePng is not null)
                return WithSize(new ImageInput(_droppedImagePng, _droppedImageFormat));

            var png = _clipboard.GetImagePng();
            if (png is null)
            {
                error = "剪贴板中没有图片。可以先复制图片，或直接把图片拖进窗口。";
                return null;
            }

            return WithSize(new ImageInput(png, ImageFormat.Png));
        }

        return new TextInput(InputText);
    }

    /// <summary>补上图片宽高：只读文件头，不解码像素。</summary>
    private static ImageInput WithSize(ImageInput input)
    {
        var (width, height) = ImageSizeReader.Read(input.Data);
        return input with { Width = width, Height = height };
    }

    private static string PreviewInput(IToolInput input) => input switch
    {
        TextInput text => text.Text.Length <= 120 ? text.Text : text.Text[..120] + "…",
        ImageInput image => $"图片 {image.ByteSize} 字节",
        ImageSetInput images => $"{images.Images.Count} 张图片",
        FileSetInput files => $"{files.Paths.Count} 个文件",
        _ => input.GetType().Name
    };
}
