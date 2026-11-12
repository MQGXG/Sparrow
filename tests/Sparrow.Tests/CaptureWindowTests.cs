using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Shouldly;
using Sparrow.App.Views;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 截图动作栏的键盘路由。
/// 和面板一样用 headless 验证：真机 keybd_event 对字母键不稳定，
/// 不能作为「快捷键起作用」的证据。
/// </summary>
public sealed class CaptureWindowTests
{
    private static CaptureWindow CreateWithActionBar()
    {
        var window = new CaptureWindow();
        window.Show();
        window.ActionBar.IsVisible = true;
        return window;
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter, CaptureAction.Recognize)]
    [InlineData(Key.R, CaptureAction.Recognize)]
    [InlineData(Key.C, CaptureAction.CopyImage)]
    [InlineData(Key.S, CaptureAction.Save)]
    [InlineData(Key.P, CaptureAction.Pin)]
    public void 动作栏快捷键_选中对应动作并关闭窗口(Key key, CaptureAction expected)
    {
        var window = CreateWithActionBar();

        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);

        window.Action.ShouldBe(expected);
        window.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Esc_取消并关闭窗口()
    {
        var window = CreateWithActionBar();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        window.Action.ShouldBe(CaptureAction.None);
        window.IsVisible.ShouldBeFalse();
    }

    /// <summary>还没截完图时，字母键不应该提前触发动作。</summary>
    [AvaloniaFact]
    public void 动作栏未出现时_快捷键不生效()
    {
        var window = new CaptureWindow();
        window.Show();

        window.KeyPress(Key.P, RawInputModifiers.None, PhysicalKey.None, null);

        window.Action.ShouldBe(CaptureAction.None);
        window.IsVisible.ShouldBeTrue();

        window.Close();
    }

    /// <summary>中文输入法下字母键是 ImeProcessed，要按物理键还原，快捷键不能失效。</summary>
    [AvaloniaTheory]
    [InlineData(PhysicalKey.Enter, CaptureAction.Recognize)]
    [InlineData(PhysicalKey.C, CaptureAction.CopyImage)]
    [InlineData(PhysicalKey.S, CaptureAction.Save)]
    [InlineData(PhysicalKey.P, CaptureAction.Pin)]
    public void 输入法处理过的按键_仍按物理键触发动作(PhysicalKey physicalKey, CaptureAction expected)
    {
        var window = CreateWithActionBar();

        window.KeyPress(Key.ImeProcessed, RawInputModifiers.None, physicalKey, null);

        window.Action.ShouldBe(expected);
        window.IsVisible.ShouldBeFalse();
    }
}
