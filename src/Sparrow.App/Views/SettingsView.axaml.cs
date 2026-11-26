using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // 隧道阶段拦截：热键框是只读的，普通 KeyDown 可能先被 TextBox 处理掉
        AddHandler(KeyDownEvent, OnHotkeyKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// 热键录制：点输入框后直接按组合键写入文本；Backspace / Delete 清空。
    /// 只处理 Tag="hotkey" 的输入框，不影响其他输入。
    /// </summary>
    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { Tag: "hotkey" } box)
            return;

        if (e.Key is Key.Back or Key.Delete)
        {
            box.Text = string.Empty;
            e.Handled = true;
            return;
        }

        if (!HotkeyCapture.TryFormat(e.KeyModifiers, e.Key, out var text))
            return;

        box.Text = text;
        box.CaretIndex = text.Length;
        e.Handled = true;
    }

    private void OnSaveHotkeysClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.SaveHotkeys();
    }

    private void OnOpenLogsClick(object? sender, RoutedEventArgs e) =>
        OpenFolder(SparrowPaths.Logs);

    private void OnOpenDataClick(object? sender, RoutedEventArgs e)
    {
        var path = DataContext is SettingsViewModel viewModel
            ? viewModel.DataDirectoryDisplay
            : SparrowPaths.DataRoot;

        OpenFolder(path);
    }

    /// <summary>选择新的数据目录；只写设置，重启后生效（不搬旧文件）。</summary>
    private async void OnChangeDataDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 Sparrow 数据目录",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            viewModel.DataDirectory = path;
    }

    private void OnOpenPluginsClick(object? sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(AppContext.BaseDirectory, "plugins"));

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            await viewModel.RescanAsync();
    }

    private async void OnSaveAiClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            await viewModel.TestAiAsync();
    }

    private void OnSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }
            && DataContext is SettingsViewModel viewModel
            && Enum.TryParse<SettingsSection>(tag, out var section))
        {
            viewModel.SelectSection(section);
        }
    }

    private void OnClearAiKeyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.ClearAiKey();
    }

    // 离线模型：下载 / 删除都在宿主完成，这里只转发给行 VM
    private async void OnDownloadLocalModelClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LocalModelItemViewModel item)
            await item.EnsureAsync();
    }

    private async void OnDeleteLocalModelClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LocalModelItemViewModel item)
            await item.DeleteAsync();
    }

    private void OnSaveModelMirrorClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.SaveLocalModelMirror();
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            await viewModel.CheckForUpdatesAsync();
    }

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
