using Shouldly;
using Sparrow.App.Settings;
using Xunit;

namespace Sparrow.Tests;

public sealed class HotkeyTextTests
{
    [Theory]
    [InlineData("Ctrl+Alt+S", "Ctrl+Alt+S")]
    [InlineData("ctrl+shift+d", "Ctrl+Shift+D")]
    [InlineData("F9", "F9")]
    [InlineData("Ctrl+F12", "Ctrl+F12")]
    public void 解析后格式化_还原标准写法(string input, string expected)
    {
        HotkeyText.TryParse(input, out var modifiers, out var virtualKey).ShouldBeTrue();

        HotkeyText.Format(modifiers, virtualKey).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Hello")]
    public void 非法输入_返回false(string input)
    {
        HotkeyText.TryParse(input, out _, out _).ShouldBeFalse();
    }
}
