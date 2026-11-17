using System.Text.Json;

namespace Sparrow.App.Settings;

/// <summary>
/// categories.json 的读取：只覆盖分类的「展示名」，不动插件的分类键。
///
/// 位置：<c>%LOCALAPPDATA%\Sparrow\categories.json</c>。
/// 文件缺失（默认行为）、损坏、无权限一律回退内置显示名并记日志——
/// 配置文件坏了不该让工具墙起不来。
/// </summary>
public sealed class CategoryLabelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;
    private readonly IAppLog _log;

    public CategoryLabelStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
    }

    public string Path => _path;

    /// <summary>读取自定义显示名；没有配置文件或内容为空时返回空字典。</summary>
    public IReadOnlyDictionary<string, string> Load()
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(_path))
            return labels;

        try
        {
            var config = JsonSerializer.Deserialize<CategoryConfig>(
                File.ReadAllText(_path), JsonOptions);

            if (config?.Labels is null)
                return labels;

            foreach (var (key, value) in config.Labels)
            {
                // 空键/空值没有意义，跳过而不是让界面出现空白分类名
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;

                labels[key.Trim()] = value.Trim();
            }

            return labels;
        }
        catch (Exception ex)
        {
            _log.Warn($"分类配置读取失败，使用内置显示名：{ex.Message}");
            return labels;
        }
    }

    /// <summary>categories.json 的根结构；只认 labels，其余字段忽略。</summary>
    private sealed class CategoryConfig
    {
        public Dictionary<string, string>? Labels { get; set; }
    }
}
