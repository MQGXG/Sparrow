namespace Sparrow.Plugin.Sdk;

/// <summary>
/// SDK 版本信息。插件清单里的 <c>sdkVersion</c> 与这里比对。
///
/// 规则（见 docs/plugin-model.md 第九节）：
///   major 相同 → 兼容；major 不同 → 需要重新编译插件。
/// 不做运行时 shim：契约是编译期绑定的，延迟报错只会让问题更难查。
/// </summary>
public static class SdkInfo
{
    /// <summary>当前 SDK 版本，形如 1.1。</summary>
    public const string Version = "1.1";

    /// <summary>主版本号，兼容性判断只看它。</summary>
    public const int MajorVersion = 1;
}
