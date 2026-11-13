using System.Text.Json;

namespace Sparrow.App.Settings;

/// <summary>
/// 工具参数的持久化：记住每个工具上次用的参数值。
///
/// 位置：<c>%LOCALAPPDATA%\Sparrow\parameters.json</c>。
/// 这是宿主 UI 状态，不属于插件设置，也不进 SDK 契约。
/// </summary>
public sealed class ToolParameterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, string>> _values;

    public ToolParameterStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
        _values = Load();
    }

    /// <summary>读取某个工具上次保存的参数；没有记录时返回空字典。</summary>
    public IReadOnlyDictionary<string, string> Get(string toolId)
    {
        lock (_gate)
        {
            return _values.TryGetValue(toolId, out var values)
                ? new Dictionary<string, string>(values, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>读取单个参数值；没有记录时返回 null。</summary>
    public string? GetValue(string toolId, string key) =>
        Get(toolId).TryGetValue(key, out var value) ? value : null;

    /// <summary>保存某个工具的参数；值没变化时不重复写盘。</summary>
    public void Save(string toolId, IReadOnlyDictionary<string, string> values)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, string>(values, StringComparer.Ordinal);
            if (_values.TryGetValue(toolId, out var current) && SameValues(current, next))
                return;

            _values[toolId] = next;
            Persist();
        }
    }

    private static bool SameValues(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private Dictionary<string, Dictionary<string, string>> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, JsonOptions);
            return data is null
                ? new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
                : new Dictionary<string, Dictionary<string, string>>(data, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // 参数文件坏了不该让工具集起不来，回退为空即可
            _log.Warn($"读取工具参数失败，使用默认值：{ex.Message}");
            return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(_values, JsonOptions));
        }
        catch (Exception ex)
        {
            // 参数保存失败只影响「记住上次选择」，不影响当前执行
            _log.Warn($"保存工具参数失败：{ex.Message}");
        }
    }
}
