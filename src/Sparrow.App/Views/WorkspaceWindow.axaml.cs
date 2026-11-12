using Avalonia.Controls;
using Avalonia.Interactivity;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

public partial class WorkspaceWindow : Window
{
    public WorkspaceWindow()
    {
        InitializeComponent();

        // 关窗只是藏起来，托盘还能再打开
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
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
