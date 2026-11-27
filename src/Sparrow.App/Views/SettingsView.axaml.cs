using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

        // 打赏二维码是可选资源：有图才显示，缺图不影响关于页
        Loaded += (_, _) => EnsureDonateImage();
    }

    /// <summary>设置页「重建索引」：重新读取服务写出的索引文件。</summary>
    private void OnRebuildIndexClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.RebuildIndex();
    }

    private void OnInstallIndexServiceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.InstallIndexService();
    }

    private void OnUninstallIndexServiceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.UninstallIndexService();
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

    /// <summary>数据 tab 的「选择」：按 Tag 区分根目录 / 模型 / 录制。</summary>
    private async void OnChangeDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel
            || sender is not Button { Tag: string kind })
        {
            return;
        }

        var path = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path))
            return;

        switch (kind)
        {
            case "Data":
                viewModel.DataDirectory = path;
                break;
            case "Model":
                viewModel.ModelDirectory = path;
                break;
            case "Record":
                viewModel.RecordDirectory = path;
                break;
        }
    }

    /// <summary>数据 tab 的「打开」：按 Tag 打开对应目录。</summary>
    private void OnOpenDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel
            || sender is not Button { Tag: string kind })
        {
            return;
        }

        OpenFolder(kind switch
        {
            "Data" => viewModel.DataDirectoryDisplay,
            "Model" => viewModel.ModelDirectoryDisplay,
            "Record" => viewModel.RecordDirectoryDisplay,
            "Logs" => viewModel.LogDirectoryDisplay,
            _ => SparrowPaths.DataRoot
        });
    }

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择目录",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
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

    /// <summary>外观三态预览卡：Tag 直接是设置值，ViewModel 负责归一化与持久化。</summary>
    private void OnAppearanceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string appearance } && DataContext is SettingsViewModel viewModel)
            viewModel.Appearance = appearance;
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

    /// <summary>关于页的链接卡片：Tag 是 URL，特殊值 logs 打开日志目录。</summary>
    private void OnOpenLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target })
            return;

        if (target == "logs")
        {
            OpenFolder(SparrowPaths.Logs);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不是致命问题
        }
    }

    /// <summary>复制版本号，反馈问题时用。</summary>
    private void OnCopyVersionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        var text = $"Sparrow {viewModel.VersionText}";
        viewModel.StatusMessage = new Win32Clipboard().SetText(text)
            ? $"已复制：{text}"
            : "复制失败，请手动选择版本号";
    }

    /// <summary>可选资源：Assets/wechat-pay.png 存在才显示打赏二维码。</summary>
    private void EnsureDonateImage()
    {
        var uri = new Uri("avares://Sparrow.App/Assets/wechat-pay.png");

        try
        {
            if (!AssetLoader.Exists(uri))
                return;

            DonateImage.Source = new Bitmap(AssetLoader.Open(uri));
            DonatePanel.IsVisible = true;
        }
        catch
        {
            // 二维码缺失或损坏：保持隐藏
        }
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
