using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

/// <summary>
/// 剪贴板详情内容。独立窗口和快取面板的覆盖层共用这一份视图：
/// 容器只决定它显示在哪里，数据仍来自共享的 <see cref="ClipboardHistoryViewModel"/>。
/// </summary>
public partial class ClipboardDetailView : UserControl
{
    /// <summary>「返回」或删除后需要收起当前容器（窗口关闭 / 覆盖层隐藏）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>复制完成；快取面板借此收起整个面板，主窗详情窗口可以继续留着。</summary>
    public event EventHandler? Copied;

    public ClipboardDetailView()
    {
        InitializeComponent();
    }

    private ClipboardHistoryViewModel? History => DataContext as ClipboardHistoryViewModel;

    /// <summary>覆盖层打开后把焦点放到「返回」，键盘用户可以直接 Esc / 回车。</summary>
    public void FocusBackButton() => BackButton.Focus();

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (History is { SelectedItem: { } item } clipboard)
        {
            clipboard.Copy(item);
            Copied?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipboardEntryViewModel item } && History is { } clipboard)
            clipboard.ToggleFavorite(item);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (History is { SelectedItem: { } item } clipboard)
        {
            clipboard.Delete(item);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnTagAddClick(object? sender, RoutedEventArgs e) => AddTag();

    private void OnTagBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        AddTag();
        e.Handled = true;
    }

    private void AddTag()
    {
        if (History is { } clipboard)
            clipboard.AddTag(clipboard.NewTag);
    }

    private void OnTagRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            History is { SelectedItem: { } item } clipboard)
        {
            clipboard.RemoveTag(item, tag);
        }
    }

    private void OnDetailImageTapped(object? sender, TappedEventArgs e)
    {
        if (History is { DetailImagePng: { Length: > 0 } png } &&
            TopLevel.GetTopLevel(this) is Window owner)
        {
            var zoom = new ClipboardImageZoomWindow(png, owner);
            zoom.Show(owner);
            zoom.Activate();
        }

        e.Handled = true;
    }
}
