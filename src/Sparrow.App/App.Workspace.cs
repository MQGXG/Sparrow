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
