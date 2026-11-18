namespace Sparrow.App.Services;

/// <summary>
/// 剪贴板「不记录」名单的存取。
///
/// 由设置页实现（它持有 settings.json），剪贴板页只依赖这个接口——
/// 页面 VM 不需要知道配置是怎么落盘的，测试也只需要一个假实现。
/// </summary>
public interface IClipboardExclusionStore
{
    IReadOnlyList<string> ExcludedApps { get; }

    void SaveExcludedApps(IReadOnlyList<string> apps);
}
