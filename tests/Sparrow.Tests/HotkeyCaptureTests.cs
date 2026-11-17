using Avalonia.Input;
using Shouldly;
using Sparrow.App.Settings;
using Xunit;

namespace Sparrow.Tests;

/// <summary>热键录制：组合键格式化成可保存文本；危险的单键和不支持的键被拒绝。</summary>
public sealed class HotkeyCaptureTests
{
    [Fact]
    public void 组合键_格式化()
    {
        HotkeyCapture.TryFormat(
            KeyModifiers.Control | KeyModifiers.Alt,
            Key.P,
            out var text).ShouldBeTrue();

        text.ShouldBe("Ctrl+Alt+P");
    }

    [Fact]
    public void 修饰符顺序固定_与按下顺序无关()
    {
        HotkeyCapture.TryFormat(
            KeyModifiers.Shift | KeyModifiers.Meta | KeyModifiers.Alt | KeyModifiers.Control,
            Key.D5,
            out var text).ShouldBeTrue();

        text.ShouldBe("Ctrl+Alt+Shift+Win+5");
    }

    [Fact]
    public void 功能键_允许单按()
    {
        HotkeyCapture.TryFormat(KeyModifiers.None, Key.F9, out var text).ShouldBeTrue();
        text.ShouldBe("F9");
    }

    [Fact]
    public void 无修饰的字母数字_拒绝()
    {
        HotkeyCapture.TryFormat(KeyModifiers.None, Key.P, out _).ShouldBeFalse();
        HotkeyCapture.TryFormat(KeyModifiers.None, Key.D1, out _).ShouldBeFalse();
    }

    [Fact]
    public void 只按修饰键_拒绝()
    {
        HotkeyCapture.TryFormat(KeyModifiers.Control, Key.LeftCtrl, out _).ShouldBeFalse();
        HotkeyCapture.TryFormat(KeyModifiers.Alt, Key.LeftAlt, out _).ShouldBeFalse();
    }

    [Fact]
    public void 不支持的键_拒绝()
    {
        HotkeyCapture.TryFormat(KeyModifiers.Control, Key.Left, out _).ShouldBeFalse();
        HotkeyCapture.TryFormat(KeyModifiers.Control, Key.OemPlus, out _).ShouldBeFalse();
    }
}
