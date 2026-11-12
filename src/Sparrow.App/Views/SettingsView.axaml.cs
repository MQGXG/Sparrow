using Avalonia.Controls;
using Avalonia.Interactivity;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnSaveHotkeysClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.SaveHotkeys();
    }
}
