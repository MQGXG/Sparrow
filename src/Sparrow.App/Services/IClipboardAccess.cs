namespace Sparrow.App.Services;

/// <summary>
/// 文本剪贴板访问。
/// 抽成接口的原因：ViewModel 不应该知道剪贴板是 Win32 还是 Avalonia 实现，
/// M1 换 Avalonia 的跨平台剪贴板时只需换一个实现。
/// </summary>
public interface IClipboardAccess
{
    /// <summary>读取文本；剪贴板为空或不是文本时返回 null。</summary>
    string? GetText();

    /// <summary>写入文本。返回 false 表示剪贴板被占用或系统拒绝。</summary>
    bool SetText(string text);
}
