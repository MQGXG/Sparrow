namespace Sparrow.Plugin.Window.Pin;

/// <summary>
/// 插件需要的窗口状态操作。
///
/// 拾取窗口、图钉这些「要创建窗口」的交互由宿主的 <c>IWindowOverlay</c> 负责；
/// 这里只剩纯 Win32 状态读写，抽成接口是为了让切换逻辑能在测试里复现。
/// </summary>
public interface IWindowPinApi
{
    /// <summary>读取窗口标题；没有标题返回 false。</summary>
    bool TryGetTitle(nint handle, out string title);

    bool IsTopMost(nint handle);

    /// <summary>窗口是否最大化；最大化窗口已经占满屏幕，钉了没有意义。</summary>
    bool IsMaximized(nint handle);

    /// <summary>设置/取消置顶；返回是否成功。</summary>
    bool SetTopMost(nint handle, bool topMost);
}
