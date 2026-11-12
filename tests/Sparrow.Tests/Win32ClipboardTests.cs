using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 真实 Win32 剪贴板的往返测试。
///
/// 这一层之前只有 FakeClipboard 覆盖，导致「复制静默失败」在真机上才暴露。
/// 测试会临时改写系统剪贴板，结束后恢复原内容。
/// </summary>
public sealed class Win32ClipboardTests
{
    [Fact]
    public void 写入后能原样读回()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var clipboard = new Win32Clipboard();
        var original = clipboard.GetText();
        var probe = "sparrow-clipboard-" + Guid.NewGuid().ToString("N");

        try
        {
            clipboard.SetText(probe).ShouldBeTrue();
            clipboard.GetText().ShouldBe(probe);
        }
        finally
        {
            if (original is not null)
                clipboard.SetText(original);
        }
    }

    [Fact]
    public void 写入空字符串后能读回空字符串()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var clipboard = new Win32Clipboard();
        var original = clipboard.GetText();

        try
        {
            clipboard.SetText(string.Empty).ShouldBeTrue();

            // 空字符串是合法内容，不应该被当成「没有文本」
            clipboard.GetText().ShouldBe(string.Empty);
        }
        finally
        {
            if (original is not null)
                clipboard.SetText(original);
        }
    }
}
