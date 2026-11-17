namespace Sparrow.Plugin.Window.Pin;

/// <summary>
/// 窗口置顶所需的系统操作。
///
/// 抽成接口只有一个目的：让「点窗口 → 切换置顶」的执行流程
/// 可以在没有鼠标、没有真实窗口的测试里稳定复现
/// （真实实现见 <see cref="Win32WindowPinApi"/>）。
/// </summary>
public interface IWindowPinApi
{
    /// <summary>Esc 是否被按下（取消信号）。</summary>
    bool IsCancelPressed();

    /// <summary>鼠标左键当前是否处于按下状态。</summary>
    bool IsLeftButtonDown();

    /// <summary>鼠标当前位置的最上层窗口；桌面/系统窗口返回 false。</summary>
    bool TryGetPointedWindow(out nint handle);

    /// <summary>读取窗口标题；没有标题返回 false。</summary>
    bool TryGetTitle(nint handle, out string title);

    /// <summary>窗口是否属于 Sparrow 自己（宿主或插件）。</summary>
    bool IsOwnProcessWindow(nint handle);

    bool IsTopMost(nint handle);

    /// <summary>设置/取消置顶；返回是否成功。</summary>
    bool SetTopMost(nint handle, bool topMost);
}
