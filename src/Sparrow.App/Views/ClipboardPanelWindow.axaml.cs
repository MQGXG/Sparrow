using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

/// <summary>
/// 全局热键呼出的剪贴板快取面板。
///
/// 只包含搜索、筛选和列表，不带主窗的品牌栏和页签；单击选中、双击或回车复制并收起。
/// 「查看」不再开新窗口，而是把 <see cref="ClipboardDetailView"/> 覆盖在面板上，
/// ← / Esc 返回列表，复制后整个面板收起。数据复用主窗的 <see cref="ClipboardHistoryViewModel"/>。
/// </summary>
public partial class ClipboardPanelWindow : Window
{
    /// <summary>
    /// 面板呼出前列表的搜索词。面板和工作区共用同一个 ViewModel，
    /// 呼出时清空搜索不能让工作区页跟着丢状态，收起时原样还回去。
    /// </summary>
    private string _savedQuery = string.Empty;

    /// <summary>拖出状态：按下位置、被拖条目、是否已越过阈值。</summary>
    private PointerPressedEventArgs? _dragPress;
    private ClipboardEntryViewModel? _dragItem;
    private Point _dragOrigin;
    private bool _dragStarted;

    /// <summary>拖动结束后要吃掉那一次 Tapped，避免顺带改变选中或复制。</summary>
    private bool _suppressClick;

    /// <summary>搜索行是否展开：默认收起，点标题行的 🔍 才占这一行。</summary>
    private bool _isSearchOpen;

    /// <summary>置顶状态的持久化；不给就只在会话内生效。</summary>
    internal SettingsStore? SettingsStore { get; set; }

    internal AppSettings Settings { get; set; } = new();

    /// <summary>右键菜单：与工作区页共用同一套结构，差异只有复制后是否收起。</summary>
    private readonly ClipboardEntryMenu _entryMenu;

    public ClipboardPanelWindow()
    {
        InitializeComponent();

        _entryMenu = new ClipboardEntryMenu(
            () => History,
            showDetail: item =>
            {
                if (History is { } history)
                    history.SelectedItem = item;
                ShowDetail();
            },
            afterCopy: HidePanel);

        // 隧道阶段拦 Esc / Enter，避免被搜索框先吃掉
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private ClipboardHistoryViewModel? History => DataContext as ClipboardHistoryViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowChrome.TryApplyDarkTitleBar(this);

        // 与 Snow-shot 一致：录制中打开剪贴板小窗，窗口会进入视频。

        // 恢复上次的置顶状态
        if (Settings.ClipboardPanelTopmost)
        {
            Topmost = true;
            PinIcon.Classes.Add("pinned");
        }
    }

    /// <summary>每次呼出都从干净状态开始：收起详情、清空搜索；搜索行按上次状态展开。</summary>
    public void PrepareForShow()
    {
        HideDetail(focusSearch: false);
        _savedQuery = History?.Query ?? string.Empty;
        SearchBox.Text = string.Empty;

        // 搜索行收起时不能 Focus，否则焦点落在不可见控件上，键盘没反应
        if (_isSearchOpen)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    /// <summary>收起面板；顺手收起覆盖层，避免下次呼出还停在上一条详情。</summary>
    public void HidePanel()
    {
        HideDetail(focusSearch: false);

        // 面板和工作区页共用一个 Query：面板的清空只是本地行为，收起时把搜索词还回去
        if (History is { } history && history.Query != _savedQuery)
            history.Query = _savedQuery;

        Hide();

        // 记一笔缓存现状：下次再觉得内存高，看日志就行，不用翻代码算解码尺寸
        var stats = ImageCache.Instance.Stats();
        App.Log.Info(
            $"图片缓存：缩略图 {stats.ThumbnailEntries} 张 / {stats.ThumbnailBytes / (1024 * 1024)} MB"
            + $"，详情 {stats.DetailEntries} 张 / {stats.DetailBytes / (1024 * 1024)} MB"
            + $"，预览 {stats.PreviewEntries} 张 / {stats.PreviewBytes / (1024 * 1024)} MB"
            + $"，解码失败 {stats.Failed} 张");
    }

    /// <summary>无边框窗口没有系统标题栏，标题行就是拖动区。</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 关闭按钮按下时不要顺带把窗口拖走
        if (e.Source is Visual source
            && (source is Button || source.FindAncestorOfType<Button>() is not null))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>标题行的搜索开关：默认收起，省出一行给列表。</summary>
    private void OnTitleSearchClick(object? sender, RoutedEventArgs e)
    {
        SetSearchOpen(!_isSearchOpen);
        e.Handled = true;
    }

    private void SetSearchOpen(bool open)
    {
        _isSearchOpen = open;
        SearchRow.IsVisible = open;
        ToggleClass(SearchToggleIcon, "active", open);

        if (open)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        // 收起时把搜索词还给工作区页，和收起面板同一条规则
        if (History is { } history && history.Query != _savedQuery)
            history.Query = _savedQuery;
    }

    /// <summary>标题行的图钉：切换面板是否常驻最前。</summary>
    private void OnTitlePinClick(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        ToggleClass(PinIcon, "pinned", Topmost);

        Settings = Settings with { ClipboardPanelTopmost = Topmost };
        SettingsStore?.Save(Settings);

        e.Handled = true;
    }

    private static void ToggleClass(AvaloniaObject element, string name, bool on)
    {
        if (element is not StyledElement styled)
            return;

        if (on)
            styled.Classes.Add(name);
        else
            styled.Classes.Remove(name);
    }

    /// <summary>标题行的关闭按钮：与 Esc 同一条路径，顺带把搜索词还回去。</summary>
    private void OnTitleCloseClick(object? sender, RoutedEventArgs e)
    {
        HidePanel();
        e.Handled = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 保持同一个实例，下次热键直接复用
        e.Cancel = true;
        HidePanel();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DetailLayer.IsVisible)
                HideDetail();
            else if (_isSearchOpen)
            {
                // 先收搜索行并把列表还原，再按一次才关面板
                SetSearchOpen(false);
            }
            else
                HidePanel();

            e.Handled = true;
            return;
        }

        // 详情覆盖时，Enter 属于标签框 / 按钮，不再触发「复制并收起」
        if (e.Key != Key.Enter || e.Source is Button || DetailLayer.IsVisible)
            return;

        if (History is { SelectedItem: { } item } clipboard)
        {
            clipboard.Copy(item);
            HidePanel();
        }

        e.Handled = true;
    }

    private void OnItemClick(object? sender, TappedEventArgs e)
    {
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }

        if (sender is Control { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
        {
            // Ctrl+点击 = 加入/移出顺序粘贴队列，和主窗一致
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                clipboard.ToggleQueue(item);
            else
                clipboard.SelectedItem = item;
        }
    }

    /// <summary>按住条目准备拖出；按钮上的按下交给按钮自己。</summary>
    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.Tag is not ClipboardEntryViewModel item)
            return;

        if (IsInsideButton(e.Source))
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        _dragPress = e;
        _dragItem = item;
        _dragOrigin = e.GetPosition(this);
        _dragStarted = false;
    }

    /// <summary>越过 8px 阈值后拖出；Ctrl 只给文件、Shift 只给内容，和主窗同一套约定。</summary>
    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPress is null || _dragItem is null || _dragStarted)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetDrag();
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragOrigin.X) < 8 &&
            Math.Abs(position.Y - _dragOrigin.Y) < 8)
        {
            return;
        }

        var payload = History?.GetDragPayload(_dragItem);
        if (payload is null)
        {
            ResetDrag();
            return;
        }

        _dragStarted = true;
        _suppressClick = true;

        try
        {
            var mode = ClipboardDragMode.All;

            if (_dragPress.KeyModifiers.HasFlag(KeyModifiers.Control))
                mode = ClipboardDragMode.FileOnly;
            else if (_dragPress.KeyModifiers.HasFlag(KeyModifiers.Shift))
                mode = ClipboardDragMode.InlineOnly;

            var drag = await ClipboardDragService.CreateAsync(payload, StorageProvider, mode);
            if (drag is null)
                return;

            try
            {
                await DragDrop.DoDragDropAsync(_dragPress, drag.Transfer, DragDropEffects.Copy);
            }
            finally
            {
                drag.Lifetime?.Dispose();
            }
        }
        catch
        {
            // 目标应用拒绝拖放、临时文件被占用等都不该影响面板
        }
        finally
        {
            ResetDrag();
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e) => ResetDrag();

    private void ResetDrag()
    {
        _dragPress = null;
        _dragItem = null;
        _dragStarted = false;
    }

    /// <summary>双击整行 = 复制并收起；按钮上的双击交给按钮自己。</summary>
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInsideButton(e.Source))
            return;

        if (sender is Control { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
        {
            clipboard.SelectedItem = item;
            clipboard.Copy(item);
            HidePanel();
            e.Handled = true;
        }
    }

    /// <summary>「查看」：选中条目，并把详情覆盖在面板上。</summary>
    private void OnOpenDetailClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
        {
            clipboard.SelectedItem = item;
            ShowDetail();
        }

        e.Handled = true;
    }

    private void ShowDetail()
    {
        DetailLayer.IsVisible = true;
        DetailView.FocusBackButton();
    }

    private void HideDetail(bool focusSearch = true)
    {
        if (!DetailLayer.IsVisible)
            return;

        DetailLayer.IsVisible = false;

        if (focusSearch)
            SearchBox.Focus();
    }

    private void OnDetailCloseRequested(object? sender, EventArgs e) => HideDetail();

    /// <summary>详情里点「复制」：复制后直接收起整个面板，回到刚才的应用。</summary>
    private void OnDetailCopied(object? sender, EventArgs e) => HidePanel();

    private void OnDetailBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        HideDetail();
        e.Handled = true;
    }

    private void OnKindClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardFilterViewModel filter } && History is { } clipboard)
            clipboard.SelectKind(filter);
    }

    private void OnTagFilterClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardFilterViewModel filter } && History is { } clipboard)
            clipboard.SelectTagFilter(filter);
    }

    private void OnQueueToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
            clipboard.ToggleQueue(item);

        e.Handled = true;
    }

    /// <summary>队列条上点某一条 = 移出队列。</summary>
    private void OnQueueRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardQueueItemViewModel item } && History is { } clipboard)
            clipboard.RemoveQueueItem(item);

        e.Handled = true;
    }

    /// <summary>「复制下一条」：只写剪贴板，不模拟按键，用户自己在目标应用粘贴。</summary>
    private void OnCopyNextClick(object? sender, RoutedEventArgs e)
    {
        History?.PasteNext(simulate: false);
        e.Handled = true;
    }

    private void OnQueueClearClick(object? sender, RoutedEventArgs e)
    {
        History?.ClearQueue();
        e.Handled = true;
    }

    /// <summary>行挂上共享右键菜单：模板每次实例化都会走到这里。</summary>
    private void OnRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control row)
            ClipboardEntryMenu.Attach(row, _entryMenu);

        e.Handled = true;
    }

    /// <summary>列表底部「加载更多」：每次多读一页。</summary>
    private void OnClipboardLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        History?.LoadMore();
        e.Handled = true;
    }

    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
            clipboard.ToggleFavorite(item);

        e.Handled = true;
    }

    /// <summary>工具条的「复制」：复制并收起，和双击一致。</summary>
    private void OnRowCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
        {
            clipboard.SelectedItem = item;
            clipboard.Copy(item);
            HidePanel();
        }

        e.Handled = true;
    }

    /// <summary>
    /// 工具条的「粘贴到刚才的应用」：时序逻辑与右键菜单共用，避免两处 120ms 漂移。
    /// </summary>
    private void OnRowPasteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
            ClipboardEntryMenu.PasteAfterFocusReturns(item, clipboard, HidePanel);

        e.Handled = true;
    }

    /// <summary>工具条的「打开」：链接走浏览器、路径与文件走默认程序、图片转临时文件。</summary>
    private void OnRowOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item })
            item.Open();

        e.Handled = true;
    }

    /// <summary>工具条的「删除」：删掉后留在面板，可撤销。</summary>
    private void OnRowDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
            clipboard.Delete(item);

        e.Handled = true;
    }

    private void OnUndoDeleteClick(object? sender, RoutedEventArgs e)
    {
        History?.UndoDelete();
    }

    private void OnExclusionClick(object? sender, RoutedEventArgs e)
    {
        History?.ToggleSourceExclusion();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        // 「清空」入口在 ⋯ 菜单里，确认 / 取消按钮却在搜索行；
        // 搜索行默认收起，不先展开用户会看不到确认，以为点了没反应
        if (!_isSearchOpen)
            SetSearchOpen(true);

        History?.RequestClear();
        e.Handled = true;
    }

    private void OnClearConfirmClick(object? sender, RoutedEventArgs e)
    {
        History?.ConfirmClear();
    }

    private void OnClearCancelClick(object? sender, RoutedEventArgs e)
    {
        History?.CancelClear();
    }

    private static bool IsInsideButton(object? source)
    {
        var control = source as Control;
        return control?.FindAncestorOfType<Button>(includeSelf: true) is not null;
    }
}
