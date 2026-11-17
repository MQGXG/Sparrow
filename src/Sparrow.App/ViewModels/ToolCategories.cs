namespace Sparrow.App.ViewModels;

/// <summary>
/// 插件分类的展示名。
/// 插件清单里的 Category 是稳定的英文键（dev/json/ocr），界面只负责翻译，
/// 未知分类原样显示，避免第三方插件被隐藏。
///
/// 自定义：<c>%LOCALAPPDATA%\Sparrow\categories.json</c> 的 labels 段可覆盖显示名，
/// 宿主启动时调用 <see cref="Configure"/> 注入；只影响展示，不改插件的分类键。
/// </summary>
public static class ToolCategories
{
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all"] = "全部",
        ["clipboard"] = "剪贴板",
        ["dev"] = "开发",
        ["json"] = "JSON",
        ["ocr"] = "文字识别",
        ["ai"] = "AI",
        ["text"] = "文本",
        ["data"] = "数据",
        ["image"] = "图像",
        ["images"] = "图像",
        ["file"] = "文件",
        ["files"] = "文件"
    };

    /// <summary>用户自定义显示名（categories.json 的 labels）；优先级高于内置名。</summary>
    private static Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>注入自定义显示名；宿主启动时调用一次。</summary>
    public static void Configure(IReadOnlyDictionary<string, string> overrides) =>
        _overrides = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);

    /// <summary>恢复内置显示名。测试用，避免静态状态跨用例泄漏。</summary>
    public static void Reset() => _overrides.Clear();

    public static string Display(string category) =>
        _overrides.TryGetValue(category, out var custom)
            ? custom
            : DisplayNames.TryGetValue(category, out var name)
                ? name
                : category;
}
