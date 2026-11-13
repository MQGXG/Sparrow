namespace Sparrow.App.ViewModels;

/// <summary>
/// 插件分类的展示名。
/// 插件清单里的 Category 是稳定的英文键（dev/json/ocr），界面只负责翻译，
/// 未知分类原样显示，避免第三方插件被隐藏。
/// </summary>
public static class ToolCategories
{
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all"] = "全部",
        ["dev"] = "开发",
        ["json"] = "JSON",
        ["ocr"] = "文字识别",
        ["text"] = "文本",
        ["data"] = "数据",
        ["image"] = "图像",
        ["images"] = "图像",
        ["file"] = "文件",
        ["files"] = "文件"
    };

    public static string Display(string category) =>
        DisplayNames.TryGetValue(category, out var name) ? name : category;
}
