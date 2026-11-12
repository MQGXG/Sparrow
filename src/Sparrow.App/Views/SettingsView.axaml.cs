using System.Diagnostics;
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

    private void OnOpenLogsClick(object? sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(LocalAppData, "Sparrow", "logs"));

    private void OnOpenDataClick(object? sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(LocalAppData, "Sparrow"));

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // 打不开资源管理器不是致命问题，忽略
        }
    }
}
