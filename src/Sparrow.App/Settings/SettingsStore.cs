using System.Text.Json;

namespace Sparrow.App.Settings;

/// <summary>
/// settings.json 的读写。
///
/// 位置：<c>%LOCALAPPDATA%\Sparrow\settings.json</c>。
/// 读取失败（不存在 / 损坏 / 无权限）一律回退默认值并记日志——
/// 设置文件坏了不该让工具集起不来。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    public SettingsStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
    }

    public string Path => _path;

    public AppSettings Load()
    {
        if (!File.Exists(_path))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

            if (settings is null)
            {
                _log.Warn($"设置文件内容为空，使用默认值：{_path}");
                return new AppSettings();
            }

            // 手改 JSON 可能写成 "plugins": null；归一成空字典，避免后续写入丢节点
            if (settings.Plugins is null)
                settings = settings with { Plugins = new(StringComparer.Ordinal) };

            return settings;
        }
        catch (Exception ex)
        {
            _log.Warn($"设置读取失败，使用默认值：{ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>保存设置。失败只记日志，不向调用方抛异常。</summary>
    public bool Save(AppSettings settings)
    {
        var plugins = settings.Plugins ?? new(StringComparer.Ordinal);

        lock (_gate)
        {
            // 插件设置是可变字典：序列化期间不要让它被 Set 改掉
            lock (plugins)
                return SaveCore(settings with { Plugins = plugins });
        }
    }

    /// <summary>
    /// 只更新一个插件的设置节点，其余字段以磁盘上的最新内容为准。
    /// 这样插件写设置不会覆盖用户在设置页刚改的普通设置。
    /// </summary>
    public bool SavePluginSettings(string pluginId, IReadOnlyDictionary<string, string> values)
    {
        lock (_gate)
        {
            var current = Load();
            var plugins = current.Plugins ?? new(StringComparer.Ordinal);

            if (values.Count == 0)
                plugins.Remove(pluginId);
            else
                plugins[pluginId] = new Dictionary<string, string>(values, StringComparer.Ordinal);

            return SaveCore(current with { Plugins = plugins });
        }
    }

    private bool SaveCore(AppSettings settings)
    {
        var tempPath = _path + ".tmp";

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // 先写临时文件再原子替换：进程在中途退出时不会留下半截 settings.json
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("设置保存失败", ex);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // 临时文件清不掉不影响本次保存结果
            }
        }
    }
}
