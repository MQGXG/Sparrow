using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 内存工具注册表。
///
/// M0 只做「搜索 + 排序」，不做加载、不做卸载、不做健康检查——
/// 那些属于 M1 的 plugins/ 目录扫描。接口形状按 M1 需要设计，
/// 这样换实现时上层代码不用动。
/// </summary>
public sealed class ToolRegistry
{
    private readonly IReadOnlyList<ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools) => _tools = tools.ToArray();

    public int Count => _tools.Count;

    /// <summary>
    /// 按查询串搜索工具。空查询返回默认排序（按 Order 降序）。
    /// 评分刻意做得很直白：用户预期是「名字优先」，模糊匹配会把分类词顶上去。
    /// </summary>
    public IReadOnlyList<ITool> Search(string? query, int limit = 8)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _tools
                .OrderByDescending(t => t.Descriptor.Order)
                .ThenBy(t => t.Descriptor.Name, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
        }

        var trimmed = query.Trim();

        return _tools
            .Select(tool => (Tool: tool, Score: Score(tool.Descriptor, trimmed)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Tool.Descriptor.Order)
            .Take(limit)
            .Select(x => x.Tool)
            .ToArray();
    }

    private static int Score(ToolDescriptor descriptor, string query)
    {
        // 名称权重最高：用户通常直接念工具名
        if (descriptor.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 1000;

        if (descriptor.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 800;

        if (descriptor.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 600;

        // 关键词是口语词（「美化」「压缩」），次一级
        if (descriptor.Keywords.Any(k => k.Equals(query, StringComparison.OrdinalIgnoreCase)))
            return 500;

        if (descriptor.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase)))
            return 450;

        if (descriptor.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 300;

        if (descriptor.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 100;

        return 0;
    }
}
