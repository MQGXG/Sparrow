using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.Views;

public partial class WorkspaceWindow : Window
{
    private PointerPressedEventArgs? _clipboardDragPress;
    private ClipboardEntryViewModel? _clipboardDragItem;
    private Point _clipboardDragOrigin;
    private bool _clipboardDragStarted;
    private bool _suppressClipboardClick;

    /// <summary>
    /// 右键菜单：与快速面板共用同一套结构。
    /// 差异只有两处——复制后不收起窗口、不提供「粘贴到刚才的应用」（本窗就在前台）。
    /// </summary>
    private readonly ClipboardEntryMenu _entryMenu;

    public WorkspaceWindow()
    {
        InitializeComponent();

        _entryMenu = new ClipboardEntryMenu(
            () => (DataContext as WorkspaceViewModel)?.Clipboard,
            showDetail: item =>
            {
                if ((DataContext as WorkspaceViewModel)?.Clipboard is { } history)
                    history.SelectedItem = item;
                ShowClipboardDetail();
            },
            includePaste: false);

        // 拖入图片/文本文件是工作区的输入来源之一
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Esc：工具台返回工具墙（隧道阶段，避免被输入框吃掉）
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // 关窗只是藏起来，托盘还能再打开；详情窗口跟着一起收
        Closing += (_, e) =>
        {
            HideClipboardDetail();
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
        // Ctrl+F：顶栏搜索只在工作区可用，设置页不抢
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            if (DataContext is WorkspaceViewModel { IsSettingsVisible: false })
            {
                OpenTopSearch();
                e.Handled = true;
            }

            return;
        }

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

        // 详情覆盖层：Esc 先退出详情，再谈其他
        if (ClipboardDetailLayer.IsVisible)
        {
            HideClipboardDetail();
            e.Handled = true;
            return;
        }

        if (viewModel.IsSettingsVisible)
            return;

        // 顶栏搜索展开时，Esc 收起并把过滤词一起清掉
        if (IsTopSearchOpen)
        {
            CloseTopSearch(clearQuery: true);
            e.Handled = true;
            return;
        }

        if (viewModel.IsDetailOpen)
        {
            viewModel.CloseDetail();
            e.Handled = true;
        }
    }

    private const double TopSearchCollapsedWidth = 30;
    private const double TopSearchExpandedWidth = 300;

    private bool IsTopSearchOpen => TopSearchBox.IsVisible;

    private void OnTopSearchClick(object? sender, RoutedEventArgs e)
    {
        if (IsTopSearchOpen)
            CloseTopSearch(clearQuery: true);
        else
            OpenTopSearch();

        e.Handled = true;
    }

    private void OnTopSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        CloseTopSearch(clearQuery: true);
        e.Handled = true;
    }

    private void OpenTopSearch()
    {
        if (IsTopSearchOpen)
        {
            TopSearchBox.Focus();
            return;
        }

        // 先显示再展开：宽度过渡会把它从图标后面「拉」出来
        TopSearchBox.IsVisible = true;
        TopSearchPill.Width = TopSearchExpandedWidth;
        SetClass(TopSearchPill, "open", true);
        SetClass(TopSearchButton, "active", true);

        TopSearchBox.Focus();
        TopSearchBox.CaretIndex = TopSearchBox.Text?.Length ?? 0;
    }

    private void CloseTopSearch(bool clearQuery)
    {
        if (!IsTopSearchOpen)
            return;

        if (clearQuery)
        {
            if (DataContext is WorkspaceViewModel viewModel)
                viewModel.ToolQuery = string.Empty;
            else
                TopSearchBox.Text = string.Empty;
        }

        TopSearchBox.IsVisible = false;
        TopSearchPill.Width = TopSearchCollapsedWidth;
        SetClass(TopSearchPill, "open", false);
        SetClass(TopSearchButton, "active", false);
    }

    private static void SetClass(Control control, string name, bool on)
    {
        if (on)
        {
            if (!control.Classes.Contains(name))
                control.Classes.Add(name);
        }
        else
        {
            control.Classes.Remove(name);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 与 Snow-shot 一致：录制中打开工作区，窗口会进入视频。

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

    private void OnPreviousImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.PreviousImage();

        e.Handled = true;
    }

    private void OnNextImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.NextImage();

        e.Handled = true;
    }

    /// <summary>点小缩略图：跳到那一张。</summary>
    private void OnSelectImageClick(object? sender, TappedEventArgs e)
    {
        if (sender is Control { Tag: InputImageViewModel image } && DataContext is WorkspaceViewModel viewModel)
            viewModel.SelectImage(image);

        e.Handled = true;
    }

    /// <summary>移除当前轮播的这张。</summary>
    private void OnRemoveCurrentImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { CurrentImage: { } image } viewModel)
            viewModel.RemoveInputImage(image);

        e.Handled = true;
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
            ShowClipboardDetail();
        }

        e.Handled = true;
    }

    /// <summary>双击整行 = 查看详情；按钮上的双击不抢。</summary>
    /// <summary>行挂上共享右键菜单：模板每次实例化都会走到这里。</summary>
    private void OnClipboardRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control row)
            ClipboardEntryMenu.Attach(row, _entryMenu);

        e.Handled = true;
    }

    /// <summary>列表底部「加载更多」：每次多读一页，已加载的条目复用原 VM。</summary>
    private void OnClipboardLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
            clipboard.LoadMore();

        e.Handled = true;
    }

    /// <summary>行内「复制」：写剪贴板，用户自己切窗口粘贴。</summary>
    private void OnClipboardRowCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.SelectedItem = item;
            clipboard.Copy(item);
        }

        e.Handled = true;
    }

    /// <summary>行内「打开」：链接走浏览器、路径与文件走默认程序、图片转临时文件。</summary>
    private void OnClipboardRowOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item })
            item.Open();

        e.Handled = true;
    }

    /// <summary>行内「删除」：删掉后留在列表，可撤销。</summary>
    private void OnClipboardRowDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.Delete(item);
        }

        e.Handled = true;
    }

    private void OnClipboardItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInsideButton(e.Source))
            return;

        if (sender is Control { Tag: ClipboardEntryViewModel item } &&
            DataContext is WorkspaceViewModel { Clipboard: { } clipboard })
        {
            clipboard.SelectedItem = item;
            ShowClipboardDetail();
            e.Handled = true;
        }
    }

    /// <summary>在剪贴板页内盖一层详情，和快捷键面板同一套交互。</summary>
    private void ShowClipboardDetail()
    {
        ClipboardDetailLayer.IsVisible = true;
        ClipboardDetailViewHost.FocusBackButton();
    }

    private void HideClipboardDetail()
    {
        if (!ClipboardDetailLayer.IsVisible)
            return;

        ClipboardDetailLayer.IsVisible = false;

        // 焦点交还搜索框，Esc 之后可以直接继续找条目
        if (ClipboardPage.IsVisible)
            ClipboardSearchBox.Focus();
    }

    private void OnClipboardDetailCloseRequested(object? sender, EventArgs e) =>
        HideClipboardDetail();

    private void OnClipboardDetailCopied(object? sender, EventArgs e) =>
        HideClipboardDetail();

    private void OnClipboardDetailBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        HideClipboardDetail();
        e.Handled = true;
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
            viewModel.OpenTool(recent.Tool);
    }

    private void OnShowToolsClick(object? sender, RoutedEventArgs e)
    {
        HideClipboardDetail();

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
    /// 右侧某一段：切换它的识别框显示。
    /// 点眼睛顺手把左侧轮播切到这张图——否则框还停在旧图上，看起来像没反应。
    /// </summary>
    private void OnToggleSectionBoxesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextSectionViewModel section } &&
            DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.SelectSectionImage(section);
            section.ShowBoxes = !section.ShowBoxes;
        }

        e.Handled = true;
    }

    /// <summary>结果区：只看当前这张图。</summary>
    private void OnShowCurrentSectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowAllSections = false;

        e.Handled = true;
    }

    /// <summary>结果区：看全部图片的识别结果。</summary>
    private void OnShowAllSectionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowAllSections = true;

        e.Handled = true;
    }

    /// <summary>
    /// 点左侧轮播图上的识别框：切换选中。
    /// 选中多行后底部会出现「复制选中 N 行」，一次拿多行。
    /// 吃掉事件，避免继续冒泡触发图片的全屏预览。
    /// </summary>
    private void OnOcrBoxTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: OcrLineViewModel line } &&
            DataContext is WorkspaceViewModel { CurrentSection: { } section })
        {
            line.IsSelected = !line.IsSelected;
            section.RefreshSelection();
        }

        e.Handled = true;
    }

    /// <summary>复制选中的多行识别结果。</summary>
    private void OnCopySelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { CurrentSection: { } section } viewModel)
            viewModel.CopyText(section.SelectedText);

        e.Handled = true;
    }

    /// <summary>右侧某一段：只复制这一条的识别结果（含译文，如果选了翻译）。</summary>
    private void OnCopySectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextSectionViewModel section } &&
            DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.CopySectionText(section);
        }

        e.Handled = true;
    }

    /// <summary>多图结果：展开 / 折叠某一项。</summary>
    private void OnToggleSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextSectionViewModel section })
            section.IsExpanded = !section.IsExpanded;

        e.Handled = true;
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

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        HideClipboardDetail();
        CloseTopSearch(clearQuery: true);

        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.ShowSettings();
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel viewModel)
            viewModel.HideSettings();
    }
}

