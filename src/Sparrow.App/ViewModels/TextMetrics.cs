namespace Sparrow.App.ViewModels;

/// <summary>
/// 结果区文本的统计信息。
/// 只数可见字符（不含换行符），行数按换行符切分。
/// </summary>
public static class TextMetrics
{
    /// <summary>返回「N 字 · M 行」；空文本返回空串，由绑定负责隐藏。</summary>
    public static string Summarize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var characters = 0;
        var lines = 1;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\n':
                    lines++;
                    break;
                case '\r':
                    break;
                default:
                    characters++;
                    break;
            }
        }

        return $"{characters} 字 · {lines} 行";
    }
}
