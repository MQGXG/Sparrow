using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

        // 关窗只是藏起来，托盘还能再打开
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
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

    private void OnRecentToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecentToolViewModel recent } && DataContext is WorkspaceViewModel viewModel)
            viewModel.OpenTool(recent.Tool);
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
