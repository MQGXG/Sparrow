using Avalonia.Input;

namespace Sparrow.App.Settings;

/// <summary>
/// 把一次按键（修饰符 + 主键）格式化成 settings.json 的热键文本。
///
/// 规则：
/// - 主键只接受字母、数字、F1–F24；其余键（方向键、标点）不支持
/// - 纯字母 / 纯数字必须带修饰键（否则会抢占全局输入）；F1–F24 可以单按
/// - 只按修饰键返回 false，调用方应忽略
/// </summary>
public static class HotkeyCapture
{
    public static bool TryFormat(KeyModifiers modifiers, Key key, out string text)
    {
        text = string.Empty;

        var keyText = MapKey(key);
        if (keyText is null)
            return false;

        var isFunctionKey = key is >= Key.F1 and <= Key.F24;
        var hasModifier = modifiers.HasFlag(KeyModifiers.Control)
            || modifiers.HasFlag(KeyModifiers.Alt)
            || modifiers.HasFlag(KeyModifiers.Shift)
            || modifiers.HasFlag(KeyModifiers.Meta);

        if (!hasModifier && !isFunctionKey)
            return false;

        var parts = new List<string>(5);

        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");

        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");

        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");

        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Win");

        parts.Add(keyText);

        text = string.Join("+", parts);
        return true;
    }

    private static string? MapKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        _ => null
    };
}
