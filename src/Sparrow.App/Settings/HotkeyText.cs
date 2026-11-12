using Sparrow.App.Shell;

namespace Sparrow.App.Settings;

/// <summary>
/// 热键文本与 Win32 修饰符/虚拟键互转。
/// 存储格式统一为 "Ctrl+Alt+S"，便于用户直接编辑 settings.json。
/// </summary>
public static class HotkeyText
{
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyService.ModControl;
                    break;

                case "ALT":
                    modifiers |= HotkeyService.ModAlt;
                    break;

                case "SHIFT":
                    modifiers |= HotkeyService.ModShift;
                    break;

                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyService.ModWin;
                    break;

                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        virtualKey = char.ToUpperInvariant(part[0]);
                        break;
                    }

                    if (part.Length is >= 2 and <= 3
                        && (part[0] is 'F' or 'f')
                        && int.TryParse(part[1..], out var functionKey)
                        && functionKey is >= 1 and <= 24)
                    {
                        virtualKey = (uint)(0x70 + functionKey - 1);
                        break;
                    }

                    return false;
            }
        }

        return virtualKey != 0;
    }

    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();

        if ((modifiers & HotkeyService.ModControl) != 0)
            parts.Add("Ctrl");

        if ((modifiers & HotkeyService.ModAlt) != 0)
            parts.Add("Alt");

        if ((modifiers & HotkeyService.ModShift) != 0)
            parts.Add("Shift");

        if ((modifiers & HotkeyService.ModWin) != 0)
            parts.Add("Win");

        parts.Add(virtualKey switch
        {
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x70 + 1}",
            >= 0x30 and <= 0x5A => ((char)virtualKey).ToString(),
            _ => virtualKey.ToString()
        });

        return string.Join("+", parts);
    }
}
