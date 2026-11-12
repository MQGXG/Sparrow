using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Plugins;
using Sparrow.App.Settings;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 设置页：插件管理。
///
/// 只读清单（<see cref="ToolCatalog.ScanCandidates"/>），不为了显示列表去加载 500 MB 的 OCR 引擎。
/// 工具自己的参数不在这里——它们在工具的参数区。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ToolCatalog _catalog;
    private readonly SettingsStore _store;
    private AppSettings _settings;

    public SettingsViewModel(ToolCatalog catalog, SettingsStore store, AppSettings settings)
    {
        _catalog = catalog;
        _store = store;
        _settings = settings;
        Reload();
    }

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

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

    private void Persist()
    {
        var disabled = Plugins
            .Where(plugin => !plugin.IsEnabled)
            .Select(plugin => plugin.Id)
            .ToArray();

        _settings = _settings with { DisabledPlugins = disabled };
        _store.Save(_settings);
    }
}
