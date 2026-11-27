using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sparrow.App.Services;
using Sparrow.App.Services.LanShare;
using Sparrow.App.Services.LanShare.Models;

namespace Sparrow.App.Views;

/// <summary>
/// 局域网互传窗口。
///
/// 只负责展示设备、触发发送；接收由 <see cref="LocalSendServiceManager"/> 常驻完成。
/// 第一次打开时开始发现，关闭时解除订阅，不销毁后台服务。
/// </summary>
public partial class LanShareWindow : Window
{
    private readonly LocalSendServiceManager _manager = LocalSendServiceManager.Instance;
    private readonly LocalSendSettingsModel _settings;
    private readonly ObservableCollection<LocalSendDeviceInfo> _devices = [];

    public LanShareWindow()
        : this(new LocalSendSettingsModel())
    {
    }

    public LanShareWindow(LocalSendSettingsModel settings)
    {
        _settings = settings;
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // 标题栏跟随应用主题，避免深色界面配白色标题栏
        WindowChrome.TryApplyTitleBarTheme(
            this,
            !Equals(ActualThemeVariant, Avalonia.Styling.ThemeVariant.Light));

        _manager.FileReceived += OnFileReceived;
        _manager.ProgressChanged += OnReceiveProgressChanged;

        if (_manager.DiscoveryService is { } discovery)
            discovery.DeviceListChanged += OnDeviceListChanged;

        RefreshLocalInfo();
        RefreshDevices();

        try
        {
            await _manager.DiscoverDevicesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"发现设备失败：{ex.Message}";
        }

        RefreshDevices();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _manager.FileReceived -= OnFileReceived;
        _manager.ProgressChanged -= OnReceiveProgressChanged;

        if (_manager.DiscoveryService is { } discovery)
            discovery.DeviceListChanged -= OnDeviceListChanged;
    }

    private void RefreshLocalInfo()
    {
        var info = _manager.DiscoveryService?.LocalInfo;
        var directory = LocalSendServerHelper.ResolveDownloadDirectory(_settings.DownloadDirectory);

        LocalInfoText.Text = info is null
            ? "互传服务未启动：请确认设置里的局域网互传已开启。"
            : $"本机：{info.Alias} · {info.Protocol}://{info.Port}";

        ReceiveDirectoryText.Text = $"接收目录：{directory}";
    }

    private void RefreshDevices()
    {
        var discovered = _manager.DiscoveryService?.DiscoveredDevices;
        if (discovered is null)
            return;

        var selected = DeviceList.SelectedItem as LocalSendDeviceInfo;

        _devices.Clear();
        foreach (var device in discovered.OrderBy(device => device.Alias, StringComparer.OrdinalIgnoreCase))
            _devices.Add(device);

        if (selected is not null)
        {
            var match = _devices.FirstOrDefault(device => device.DiscoveryKey == selected.DiscoveryKey);
            if (match is not null)
                DeviceList.SelectedItem = match;
        }

        StatusText.Text = _devices.Count == 0
            ? "没有发现设备。确认对方也开着 LocalSend/Sparrow，或输入 IP 后刷新。"
            : $"发现 {_devices.Count} 台设备";
    }

    private void OnDeviceListChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshDevices);

    private void OnDeviceSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        SendButton.IsEnabled = DeviceList.SelectedItem is LocalSendDeviceInfo;

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在发现设备…";

        try
        {
            await _manager.DiscoverDevicesAsync();
            RefreshDevices();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"发现设备失败：{ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not LocalSendDeviceInfo device)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发送的文件",
            AllowMultiple = true
        });

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .Cast<string>()
            .ToArray();

        if (paths.Length == 0)
            return;

        SendButton.IsEnabled = false;
        SendProgress.Value = 0;
        StatusText.Text = $"正在发送到 {device.Alias}…";

        try
        {
            var pin = string.IsNullOrWhiteSpace(PinBox.Text) ? null : PinBox.Text.Trim();

            var (result, error) = await _manager.SendFilesAsync(
                device,
                paths,
                pin,
                progress => Dispatcher.UIThread.Post(() => UpdateSendProgress(progress)));

            StatusText.Text = result == LocalSendSendResult.Success
                ? $"发送完成：{paths.Length} 个文件"
                : $"发送失败：{result}{(string.IsNullOrEmpty(error) ? string.Empty : "，" + error)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"发送失败：{ex.Message}";
        }
        finally
        {
            SendProgress.Value = 0;
            SendButton.IsEnabled = DeviceList.SelectedItem is LocalSendDeviceInfo;
        }
    }

    private void UpdateSendProgress(LocalSendSendProgressArgs progress)
    {
        SendProgress.Value = progress.TotalBytes <= 0
            ? 0
            : progress.BytesSent * 100d / progress.TotalBytes;

        StatusText.Text = progress.Stage switch
        {
            LocalSendTransferStage.CalculatingChecksum => $"正在计算校验：{progress.FileName}",
            LocalSendTransferStage.WaitingForConfirmation => $"等待对方确认：{progress.FileName}",
            LocalSendTransferStage.VerifyingChecksum => $"正在校验：{progress.FileName}",
            _ => $"发送中：{progress.FileName}（{progress.FileIndex}/{progress.TotalFiles}）"
        };
    }

    private void OnReceiveProgressChanged(object? sender, LocalSendProgressArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            SendProgress.Value = e.TotalBytes <= 0 ? 0 : e.BytesTransferred * 100d / e.TotalBytes;
            StatusText.Text = $"接收中：{e.FileName}";
        });

    private void OnFileReceived(object? sender, (string FileId, string Path) e) =>
        Dispatcher.UIThread.Post(() =>
        {
            SendProgress.Value = 0;
            StatusText.Text = $"已接收：{e.Path}";
        });
}
