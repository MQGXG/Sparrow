using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 设置页：插件管理 + 热键 + 开机自启。
///
/// 工具自己的参数不在这里——它们在工具的参数区。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ToolCatalog _catalog;
    private readonly SettingsStore _store;
    private readonly StartupService _startup;
    private AppSettings _settings;

    public SettingsViewModel(
        ToolCatalog catalog,
        SettingsStore store,
        StartupService startup,
        AppSettings settings)
    {
        _catalog = catalog;
        _store = store;
        _startup = startup;
        _settings = settings;

        _searchHotkey = settings.SearchHotkey;
        _captureHotkey = settings.CaptureHotkey;
        _startWithWindows = startup.IsEnabled;

        Reload();
    }

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    [ObservableProperty]
    private string _searchHotkey = "Ctrl+Alt+S";

    [ObservableProperty]
    private string _captureHotkey = "Ctrl+Alt+C";

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string? _statusMessage;

    public void Reload()
    {
        Plugins.Clear();

        foreach (var candidate in _catalog.ScanCandidates())
        {
            var manifest = candidate.Manifest;
            if (manifest is null)
                continue;

            var enabled = !_settings.DisabledPlugins.Contains(manifest.Id, StringComparer.Ordinal);

            Plugins.Add(new PluginItemViewModel(
                manifest.Id,
                manifest.Name,
                manifest.Version,
                manifest.Tools.Length,
                enabled)
            {
                Changed = Persist
            });
        }
    }

    /// <summary>解析并保存热键。重启生效——重建热键需要重启消息循环，M1 不做运行时热切换。</summary>
    public void SaveHotkeys()
    {
        if (!HotkeyText.TryParse(SearchHotkey, out var searchModifiers, out var searchKey)
            || !HotkeyText.TryParse(CaptureHotkey, out var captureModifiers, out var captureKey))
        {
            StatusMessage = "热键格式错误，示例：Ctrl+Alt+S";
            return;
        }

        _settings = _settings with
        {
            SearchHotkey = HotkeyText.Format(searchModifiers, searchKey),
            CaptureHotkey = HotkeyText.Format(captureModifiers, captureKey)
        };

        _store.Save(_settings);
        StatusMessage = "已保存，重启应用生效";
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        var ok = value ? _startup.Enable() : _startup.Disable();
        StatusMessage = ok
            ? (value ? "已启用开机自启" : "已关闭开机自启")
            : "修改开机自启失败，详情见日志";
    }

    private void Persist()
    {
        var disabled = Plugins
            .Where(plugin => !plugin.IsEnabled)
            .Select(plugin => plugin.Id)
            .ToArray();

        _settings = _settings with { DisabledPlugins = disabled };
        _store.Save(_settings);
        StatusMessage = "插件设置已保存，重启应用生效";
    }
}
