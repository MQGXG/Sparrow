using System.Text.RegularExpressions;

namespace Sparrow.App.Services;

/// <summary>
/// 离线译文的收尾清理。
///
/// 1.8B 模型偶尔会在中文句子里漏一个英文连接词（实测出现「…， thus识别质量与…」）。
/// 提示词已经要求「只使用中文」，但小模型无法百分之百遵守，所以在输出侧兜一层。
///
/// 只处理「中文之间夹着英文连接词」这一种情况：
/// · 词表只有 8 个连接词，不会碰到产品名和代码；
/// · 前后必须有中文，独立出现的英文片段（代码、URL）保持原样。
/// </summary>
internal static class TranslationCleanup
{
    private static readonly string[] Connectives =
        ["thus", "therefore", "hence", "however", "moreover", "nevertheless", "furthermore", "meanwhile"];

    private static readonly Regex NoisePattern = new(
        @"(?<=[\u4e00-\u9fff，。、；：！？）】」”])\s*(?<![A-Za-z])(?:"
        + string.Join('|', Connectives)
        + @")(?![A-Za-z])\s*(?=[\u4e00-\u9fff])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Apply(string text) =>
        string.IsNullOrEmpty(text) ? text : NoisePattern.Replace(text, string.Empty);
}
