using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 单次执行范围内的只读文件访问。
///
/// 只允许读取构造时传入的路径：路径不在白名单、文件不存在、读取失败都返回 null，
/// 由插件转成可读的错误提示。这样插件既有文件内容，又拿不到「随便读磁盘」的能力。
/// </summary>
public sealed class ScopedFileAccess : IFileAccess
{
    private readonly HashSet<string> _allowed;

    public ScopedFileAccess(IEnumerable<string> paths)
    {
        _allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var normalized = Normalize(path);
            if (normalized is not null)
                _allowed.Add(normalized);
        }
    }

    public int Count => _allowed.Count;

    public bool CanRead(string path)
    {
        var normalized = Normalize(path);
        return normalized is not null
            && _allowed.Contains(normalized)
            && File.Exists(normalized);
    }

    public byte[]? ReadAllBytes(string path)
    {
        var normalized = Normalize(path);
        if (normalized is null || !_allowed.Contains(normalized))
            return null;

        try
        {
            return File.ReadAllBytes(normalized);
        }
        catch (Exception)
        {
            // 文件被占用、权限不足、路径失效都走这里：
            // 插件拿到 null 会给出「无法读取」的提示，不需要区分底层原因。
            return null;
        }
    }

    /// <summary>统一成绝对路径；空白或非法路径返回 null。</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
