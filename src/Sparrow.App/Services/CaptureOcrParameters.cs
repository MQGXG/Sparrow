namespace Sparrow.App.Services;

/// <summary>
/// 截图 OCR 流程唯一允许接触的三个插件参数键。
///
/// 这是宿主与 OCR 插件之间的已知桥：截图工具条需要在执行前覆盖「翻译 / 引擎 / 输出」
/// 三个选项，而普通工具参数由 <see cref="Sparrow.Plugin.Sdk.ToolDescriptor.Parameters"/> 驱动。
/// 集中到这里，避免参数键散落在宿主各处；PluginBoundaryTests 会阻止新增散落键。
/// </summary>
internal static class CaptureOcrParameters
{
    public const string Translate = "translate";

    public const string Engine = "engine";

    public const string Output = "output";
}
