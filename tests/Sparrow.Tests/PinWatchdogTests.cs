using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 置顶看门狗：宿主被强杀后，靠 stdin 关闭触发恢复。
/// 只恢复「句柄还在、进程没变」的窗口，避免 HWND 复用误伤。
/// </summary>
public sealed class PinWatchdogTests
{
    private sealed class FakeNative : IPinWatchdogNative
    {
        public Dictionary<nint, uint> Windows { get; } = [];

        public List<nint> Cleared { get; } = [];

        public bool Exists(nint handle) => Windows.ContainsKey(handle);

        public uint GetProcessId(nint handle) =>
            Windows.TryGetValue(handle, out var processId) ? processId : 0;

        public void ClearTopMost(nint handle) => Cleared.Add(handle);
    }

    [Fact]
    public void 连接关闭_恢复所有仍匹配的窗口()
    {
        var native = new FakeNative();
        native.Windows[(nint)100] = 7;
        native.Windows[(nint)200] = 8;

        using var input = new StringReader("pin 100 7\npin 200 8\n");

        PinWatchdog.Run(input, native);

        native.Cleared.ShouldContain((nint)100);
        native.Cleared.ShouldContain((nint)200);
    }

    [Fact]
    public void 已取消的窗口_不再恢复()
    {
        var native = new FakeNative();
        native.Windows[(nint)100] = 7;
        native.Windows[(nint)200] = 8;

        using var input = new StringReader("pin 100 7\npin 200 8\nunpin 100\n");

        PinWatchdog.Run(input, native);

        native.Cleared.ShouldHaveSingleItem().ShouldBe((nint)200);
    }

    [Fact]
    public void 句柄复用_进程不匹配就不动()
    {
        var native = new FakeNative();
        native.Windows[(nint)100] = 99; // 句柄还在，但已经是别的进程

        using var input = new StringReader("pin 100 7\n");

        PinWatchdog.Run(input, native);

        native.Cleared.ShouldBeEmpty();
    }

    [Fact]
    public void clear命令_立即恢复并清空记录()
    {
        var native = new FakeNative();
        native.Windows[(nint)100] = 7;

        using var input = new StringReader("pin 100 7\nclear\n");

        PinWatchdog.Run(input, native);

        native.Cleared.ShouldHaveSingleItem().ShouldBe((nint)100);
    }
}
