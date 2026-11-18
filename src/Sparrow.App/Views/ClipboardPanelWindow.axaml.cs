using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sparrow.App.Services;
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
    public ClipboardPanelWindow()
    {
        InitializeComponent();

        // 隧道阶段拦 Esc / Enter，避免被搜索框先吃掉
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private ClipboardHistoryViewModel? History => DataContext as ClipboardHistoryViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowChrome.TryApplyDarkTitleBar(this);
    }

    /// <summary>每次呼出都从干净状态开始：收起详情、清空搜索、光标落到搜索框。</summary>
    public void PrepareForShow()
    {
        HideDetail(focusSearch: false);
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>收起面板；顺手收起覆盖层，避免下次呼出还停在上一条详情。</summary>
    public void HidePanel()
    {
        HideDetail(focusSearch: false);
        Hide();
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
        if (sender is Control { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
        {
            // Ctrl+点击 = 加入/移出顺序粘贴队列，和主窗一致
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                clipboard.ToggleQueue(item);
            else
                clipboard.SelectedItem = item;
        }
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

    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
            clipboard.ToggleFavorite(item);

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
        History?.RequestClear();
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
