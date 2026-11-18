using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

public partial class WorkspaceWindow : Window
{
    public WorkspaceWindow()
    {
        InitializeComponent();

        // 拖入图片/文本文件是工作区的输入来源之一
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Esc：工具台返回工具墙（隧道阶段，避免被输入框吃掉）
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // 关窗只是藏起来，托盘还能再打开
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>
    /// Esc 只负责「工具台 → 工具墙」这一层返回。
    /// 设置页有自己的「← 返回」，不抢；其余状态不拦截。
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (DataContext is not WorkspaceViewModel viewModel)
            return;

        if (viewModel.IsImagePreviewVisible)
        {
            viewModel.HideImagePreview();
            e.Handled = true;
            return;
        }

        if (viewModel.IsSettingsVisible)
            return;

        if (viewModel.IsDetailOpen)
        {
            viewModel.CloseDetail();
            e.Handled = true;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 让 Windows 标题栏跟随应用的深色主题
        WindowChrome.TryApplyDarkTitleBar(this);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // 设置页不接收文件，避免拖放穿透到后面的工作区
        if (DataContext is WorkspaceViewModel { IsSettingsVisible: true })
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { IsSettingsVisible: false } viewModel)
        {
            var paths = e.DataTransfer.TryGetFiles()?
                .Select(item => item.TryGetLocalPath())
                .Where(path => path is not null)
                .Cast<string>()
                .ToArray();

            if (paths is { Length: > 0 })
                viewModel.AcceptDroppedFiles(paths);
        }

        e.Handled = true;
    }

    private void OnClearDroppedImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ClearDroppedImage();
    }

    private void OnClearDroppedFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ClearDroppedFiles();
    }

    /// <summary>文件类工具的选择入口：和拖入走同一条记录路径。</summary>
    private async void OnChooseFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要处理的文件",
            AllowMultiple = true
        });

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();

        if (paths.Length > 0)
            viewModel.AcceptPickedFiles(paths);
    }

    /// <summary>图片集的选择入口：多选，只收图片。</summary>
    private async void OnChooseImagesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel viewModel)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择图片",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp", "image/gif"]
                }
            ]
        });

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();

        if (paths.Length > 0)
            viewModel.AcceptPickedImages(paths);
    }

    private void OnPasteImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.AddClipboardImage();
    }

    private void OnRemoveInputImageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InputImageViewModel image } && DataContext is WorkspaceViewModel viewModel)
            viewModel.RemoveInputImage(image);

        // 别让移除按钮的点击继续冒泡去打开大图
        e.Handled = true;
    }

    private void OnPreviewInputImageClick(object? sender, TappedEventArgs e)
    {
        if (sender is Control { Tag: InputImageViewModel image } && DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowImagePreview(image);
    }

    private void OnClearInputImagesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ClearInputImages();
    }

    private void OnCloseImagePreviewClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.HideImagePreview();
    }

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToolItemViewModel tool } && DataContext is WorkspaceViewModel viewModel)
            viewModel.OpenTool(tool);
    }

    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CategoryViewModel category } && DataContext is WorkspaceViewModel viewModel)
            viewModel.SelectCategory(category.Name == "全部" ? null : category.Name);
    }

    /// <summary>点组标题：折叠/展开该分类；手动操作会退出 chip 联动。</summary>
    private void OnGroupHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToolGroupViewModel group } && DataContext is WorkspaceViewModel viewModel)
            viewModel.ToggleGroup(group.Category);
    }

    private void OnRecentToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecentToolViewModel recent } && DataContext is WorkspaceViewModel viewModel)
            viewModel.OpenTool(recent.Tool, fromRecent: true);
    }

    private void OnShowRecentClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowRecent();
    }

    private void OnShowToolsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowTools();
    }

    private void OnBackToWallClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.CloseDetail();
    }

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            await viewModel.ExecuteAsync();
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.CopyResult();
    }

    /// <summary>
    /// 保存图片结果（缩放 / 格式转换的产物）。
    /// 落盘是宿主的事——插件按契约不写盘，只在结果里带字节。
    /// </summary>
    private async void OnSaveResultClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel { HasImageResult: true } viewModel
            || viewModel.ImageResultPng is not { Length: > 0 } bytes)
            return;

        var (extension, description) = viewModel.ImageResultFormat switch
        {
            Sparrow.Plugin.Sdk.ImageFormat.Jpeg => ("jpg", "JPEG 图片"),
            Sparrow.Plugin.Sdk.ImageFormat.Webp => ("webp", "WebP 图片"),
            _ => ("png", "PNG 图片")
        };

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存图片",
                SuggestedFileName = $"Sparrow-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultExtension = extension,
                FileTypeChoices =
                [
                    new FilePickerFileType(description) { Patterns = [$"*.{extension}"] }
                ]
            });

            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            // 保存失败不打断工作区：结果还在，可以再试或改用复制
            App.Log.Error("保存图片结果失败", ex);
        }
    }

    private void OnSvgPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowSvgPreview();
    }

    private void OnSvgSourceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowSvgSourceView();
    }

    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HistoryEntryViewModel history } && DataContext is WorkspaceViewModel viewModel)
            viewModel.CopyHistory(history);
    }

    private void OnClearHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ClearHistory();
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowSettings();
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.HideSettings();
    }
}
