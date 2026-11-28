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
    /// <summary>
    /// 当前 SDK 版本，形如 1.10。
    /// 1.8：新增 <c>IToolHost.LocalModels</c>（本地模型下载与缓存）。
    /// 1.9：新增 <c>IToolHost.LocalTranslation</c>（宿主共享的本地翻译引擎）。
    /// 1.10：新增 <c>IToolHost.OnDispose</c>（卸载时回收插件副作用）。
    /// </summary>
    public const string Version = "1.10";

    /// <summary>主版本号，兼容性判断只看它。</summary>
    public const int MajorVersion = 1;

    /// <summary>
    /// SDK 程序集名前缀。宿主必须由默认 ALC 解析它，
    /// 否则 <c>ITool</c> 会分裂成两个类型，插件静默不注册。
    /// </summary>
    public const string SdkAssemblyPrefix = "Sparrow.Plugin.Sdk";

    /// <summary>
    /// 插件 UI 契约程序集名前缀（P4 引入）。
    /// 与 SDK 一样必须由默认 ALC 解析，否则界面基类也会分裂。
    /// </summary>
    public const string UiContractAssemblyPrefix = "Sparrow.Plugin.Ui";
}
