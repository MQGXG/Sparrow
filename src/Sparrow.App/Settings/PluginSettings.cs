using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Settings;

/// <summary>
/// 插件的 <see cref="IToolSettings"/> 实现：内存里读写，写盘时只更新
/// <c>settings.json</c> 的 <c>plugins.{pluginId}</c> 节点。
///
/// 这样插件设置与普通设置共用一份文件，但插件写设置不会覆盖用户在设置页
/// 刚改的字段——<see cref="SettingsStore.SavePluginSettings"/> 会以磁盘上的
/// 最新内容为基准，只替换目标插件节点。
/// </summary>
public sealed class PluginSettings : IToolSettings
{
    private readonly string _pluginId;
    private readonly Dictionary<string, Dictionary<string, string>> _plugins;
    private readonly SettingsStore _store;

    public PluginSettings(
        string pluginId,
        Dictionary<string, Dictionary<string, string>> plugins,
        SettingsStore store)
    {
        _pluginId = pluginId;
        _plugins = plugins;
        _store = store;
    }

    public string? Get(string key)
    {
        lock (_plugins)
        {
            return _plugins.TryGetValue(_pluginId, out var values)
                   && values.TryGetValue(key, out var value)
                ? value
                : null;
        }
    }

    public void Set(string key, string? value)
    {
        Dictionary<string, string> snapshot;

        lock (_plugins)
        {
            if (!_plugins.TryGetValue(_pluginId, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                _plugins[_pluginId] = values;
            }

            if (value is null)
                values.Remove(key);
            else
                values[key] = value;

            // 空节点不留在 settings.json 里，避免禁用插件后留下永远不用的空对象
            if (values.Count == 0)
                _plugins.Remove(_pluginId);

            snapshot = new Dictionary<string, string>(values, StringComparer.Ordinal);
        }

        // 先释放字典锁再写盘：SettingsStore 内部有独立的写锁
        _store.SavePluginSettings(_pluginId, snapshot);
    }
}
