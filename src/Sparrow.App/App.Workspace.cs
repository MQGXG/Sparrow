// App 分部类：剪贴板面板与工作区的打开、释放。
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Sparrow.App.History;
using Sparrow.App.Overlays;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Services.LanShare;
using Sparrow.App.Services.LanShare.Models;
using Sparrow.App.Settings;
using Sparrow.App.Shell;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App;

public partial class App
{
    /// <summary>
    /// 热键呼出 / 收起剪贴板快取面板。面板首次使用时才创建，之后复用同一实例。
    /// 数据直接取主窗的剪贴板视图模型，复制后主窗刷新时保持一致。
    /// </summary>
    private void ToggleClipboardPanel()
    {
        if (_workspaceViewModel?.Clipboard is not { } clipboard)
            return;

        if (_clipboardPanel is { IsVisible: true })
        {
            _clipboardPanel.HidePanel();
            return;
        }

        _clipboardPanel ??= new ClipboardPanelWindow
        {
            DataContext = clipboard,
            SettingsStore = _settingsStore,
            Settings = _appSettings,
        };

        clipboard.Refresh();
        _clipboardPanel.Show();
        _clipboardPanel.Activate();
        _clipboardPanel.PrepareForShow();
    }

    /// <summary>从托盘打开工作区。窗口第一次打开时才创建，之后复用。</summary>
    private void ShowWorkspace()
    {
        if (_workspaceViewModel is null)
            return;

        CancelWorkspaceRelease();

        if (_workspace is null)
        {
            _workspace = new WorkspaceWindow { DataContext = _workspaceViewModel };
            _workspace.Closing += (_, _) => ScheduleWorkspaceRelease();
        }

        _workspace.Show();
        _workspace.Activate();
        Log.Info($"工作台打开：{MemoryText()}");

        // 上次停留在剪贴板页时，隐藏期间可能释放过列表，重新读一遍
        if (_workspaceViewModel.IsClipboardOpen)
            _workspaceViewModel.Clipboard?.Refresh();

        _ = _workspaceViewModel.ReloadAsync();
    }

    /// <summary>
    /// 收到上传请求：免确认模式直接接收；否则弹确认框。
    /// 事件来自后台线程，弹窗必须切回 UI 线程。
    /// </summary>
    private void OnLanShareUploadRequested(object? sender, LocalSendUploadRequestArgs e)
    {
        if (_appSettings.LanShare.QuickSave)
        {
            e.Respond(true);
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var files = e.Dto.Files.Values
                    .Select(file => file.FileName)
                    .ToArray();

                var dialog = new LanShareConfirmWindow(e.Dto.Info.Alias, files);

                Window? owner = _lanShare is { IsVisible: true } ? _lanShare
                    : _workspace is { IsVisible: true } ? _workspace
                    : _quickSearch is { IsVisible: true } ? _quickSearch
                    : null;

                bool accepted;

                if (owner is not null)
                {
                    accepted = await dialog.ShowDialog<bool>(owner);
                }
                else
                {
                    // 没有任何窗口可当 owner 时独立显示，关闭即视为拒绝
                    var completion = new TaskCompletionSource<bool>();
                    dialog.Closed += (_, _) => completion.TrySetResult(dialog.Accepted);
                    dialog.Show();
                    accepted = await completion.Task;
                }

                e.Respond(accepted);
            }
            catch (Exception ex)
            {
                Log.Warn($"局域网互传确认失败：{ex.Message}");
                e.Respond(false);
            }
        });
    }

    /// <summary>从托盘打开局域网互传窗口；第一次打开时创建，之后复用。</summary>
    private void ShowLanShare() => ShowLanShareCore(null);

    /// <summary>搜索结果右键「发送到设备…」：打开互传窗口并带入待发送文件。</summary>
    public void OpenLanShareWithFiles(IReadOnlyList<string> files)
    {
        Log.Info($"打开局域网互传：{files.Count} 个待发送文件");
        ShowLanShareCore(files);
    }

    private void ShowLanShareCore(IReadOnlyList<string>? files)
    {
        if (_lanShare is { IsVisible: true })
        {
            if (files is { Count: > 0 })
                _lanShare.SetPendingFiles(files);

            _lanShare.Activate();
            return;
        }

        _lanShare ??= new LanShareWindow();

        if (files is { Count: > 0 })
            _lanShare.SetPendingFiles(files);

        _lanShare.Show();
        _lanShare.Activate();
    }

    /// <summary>工作台隐藏 60 秒后释放剪贴板条目/详情图和图片缓存；输入与结果保留。</summary>
    private void ScheduleWorkspaceRelease()
    {
        CancelWorkspaceRelease();

        var cts = new CancellationTokenSource();
        _workspaceRelease = cts;
        _ = ReleaseWorkspaceAfterDelayAsync(cts.Token);
    }

    private async Task ReleaseWorkspaceAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_workspace?.IsVisible == true || _clipboardPanel?.IsVisible == true)
            return;

        _workspaceViewModel?.ReleaseTransientState();
        Log.Info($"工作台空闲释放：{MemoryText()}");
    }

    /// <summary>状态切换日志统一格式，方便复测时直接对着日志看六个时点。</summary>
    private static string MemoryText() =>
        $"WorkingSet={Environment.WorkingSet / 1048576}MB, "
        + $"Managed={GC.GetTotalMemory(false) / 1048576}MB";

    private void CancelWorkspaceRelease()
    {
        _workspaceRelease?.Cancel();
        _workspaceRelease?.Dispose();
        _workspaceRelease = null;
    }
}
