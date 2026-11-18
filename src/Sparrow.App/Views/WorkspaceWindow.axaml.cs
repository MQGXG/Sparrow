using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

public partial class WorkspaceWindow : Window
{
    private PointerPressedEventArgs? _clipboardDragPress;
    private ClipboardEntryViewModel? _clipboardDragItem;
    private Point _clipboardDragOrigin;
    private bool _clipboardDragStarted;
    private bool _suppressClipboardClick;
    private ClipboardDetailWindow? _clipboardDetail;

    public WorkspaceWindow()
    {
        InitializeComponent();

        // 拖入图片/文本文件是工作区的输入来源之一
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Esc：工具台返回工具墙（隧道阶段，避免被输入框吃掉）
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // 关窗只是藏起来，托盘还能再打开；详情窗口跟着一起收
        Closing += (_, e) =>
        {
            CloseClipboardDetail();
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

    private void OnShowClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowClipboard();
    }

    private void OnClipboardKindClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardFilterViewModel filter } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.SelectKind(filter);
        }
    }

    private void OnClipboardTagFilterClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardFilterViewModel filter } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.SelectTagFilter(filter);
        }
    }

    private void OnClipboardItemClick(object? sender, TappedEventArgs e)
    {
        if (_suppressClipboardClick)
        {
            _suppressClipboardClick = false;
            return;
        }

        if (sender is Control { Tag: ClipboardEntryViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            // Ctrl+点击 = 加入/移出顺序粘贴队列
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                clipboard.ToggleQueue(item);
            else
                clipboard.SelectedItem = item;
        }
    }

    /// <summary>行内「查看」：选中条目并在独立窗口里打开详情。</summary>
    private void OnClipboardOpenDetailClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.SelectedItem = item;
            ShowClipboardDetail(clipboard);
        }

        e.Handled = true;
    }

    /// <summary>双击整行 = 查看详情；按钮上的双击不抢。</summary>
    private void OnClipboardItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInsideButton(e.Source))
            return;

        if (sender is Control { Tag: ClipboardEntryViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.SelectedItem = item;
            ShowClipboardDetail(clipboard);
            e.Handled = true;
        }
    }

    /// <summary>详情窗口全局只留一个；已开着就前置。</summary>
    private void ShowClipboardDetail(ClipboardHistoryViewModel clipboard)
    {
        if (_clipboardDetail is { IsVisible: true })
        {
            _clipboardDetail.Activate();
            return;
        }

        _clipboardDetail = new ClipboardDetailWindow { DataContext = clipboard };
        _clipboardDetail.Closed += (_, _) => _clipboardDetail = null;
        _clipboardDetail.Show(this);
    }

    private void CloseClipboardDetail()
    {
        _clipboardDetail?.Close();
        _clipboardDetail = null;
    }

    private void OnClipboardQueueToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.ToggleQueue(item);
        }

        e.Handled = true;
    }

    private void OnClipboardQueueRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardQueueItemViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.RemoveQueueItem(item);
        }
    }

    private void OnClipboardQueueClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
            clipboard.ClearQueue();
    }

    private void OnClipboardCopyNextClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
            clipboard.PasteNext(simulate: false);
    }

    private void OnClipboardUndoDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
            clipboard.UndoDelete();
    }

    private void OnClipboardExclusionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
            clipboard.ToggleSourceExclusion();
    }

    private void OnClipboardItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.Tag is not ClipboardEntryViewModel item)
            return;

        // 星标 / 队列 / 移除按钮自己处理点击，不要触发拖拽
        if (IsInsideButton(e.Source))
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        _clipboardDragPress = e;
        _clipboardDragItem = item;
        _clipboardDragOrigin = e.GetPosition(this);
        _clipboardDragStarted = false;
    }

    private async void OnClipboardItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_clipboardDragPress is null || _clipboardDragItem is null || _clipboardDragStarted)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetClipboardDrag();
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _clipboardDragOrigin.X) < 8 &&
            Math.Abs(position.Y - _clipboardDragOrigin.Y) < 8)
        {
            return;
        }

        var payload = (DataContext as WorkspaceViewModel)?.Clipboard?.GetDragPayload(_clipboardDragItem);
        if (payload is null)
        {
            ResetClipboardDrag();
            return;
        }

        _clipboardDragStarted = true;
        _suppressClipboardClick = true;

        try
        {
            var mode = ClipboardDragMode.All;

            if (_clipboardDragPress.KeyModifiers.HasFlag(KeyModifiers.Control))
                mode = ClipboardDragMode.FileOnly;
            else if (_clipboardDragPress.KeyModifiers.HasFlag(KeyModifiers.Shift))
                mode = ClipboardDragMode.InlineOnly;

            var drag = await ClipboardDragService.CreateAsync(payload, StorageProvider, mode);
            if (drag is null)
                return;

            try
            {
                await DragDrop.DoDragDropAsync(_clipboardDragPress, drag.Transfer, DragDropEffects.Copy);
            }
            finally
            {
                drag.Lifetime?.Dispose();
            }
        }
        catch
        {
            // 目标应用拒绝拖放、临时文件被占用等都不该影响页面
        }
        finally
        {
            ResetClipboardDrag();
        }
    }

    private void OnClipboardItemPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ResetClipboardDrag();

    private void ResetClipboardDrag()
    {
        _clipboardDragPress = null;
        _clipboardDragItem = null;
        _clipboardDragStarted = false;
    }

    private static bool IsInsideButton(object? source)
    {
        var control = source as Control;
        return control?.FindAncestorOfType<Button>(includeSelf: true) is not null;
    }

    private void OnClipboardFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.ToggleFavorite(item);
        }

        // 星标不该同时触发整行选中
        e.Handled = true;
    }

    private void OnClipboardClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
            clipboard.RequestClear();
    }

    private void OnClipboardClearConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
            clipboard.ConfirmClear();
    }

    private void OnClipboardClearCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
            clipboard.CancelClear();
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
        CloseClipboardDetail();

        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowRecent();
    }

    private void OnShowToolsClick(object? sender, RoutedEventArgs e)
    {
        CloseClipboardDetail();

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
        CloseClipboardDetail();

        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowSettings();
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.HideSettings();
    }
}
