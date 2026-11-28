using Avalonia.Controls;

namespace Sparrow.Plugin.Ui;

/// <summary>
/// 工具自定义界面工厂（P4 的第 2 档：宿主控件组合）。
///
/// 宿主只在 UI 线程调用 <see cref="CreateView"/>；插件的 <c>ITool.ExecuteAsync</c>
/// 在后台线程运行，**不能**在里面创建控件。
///
/// 返回 <c>null</c> 表示这个工具这次想用宿主默认工具台。
/// </summary>
public interface IToolViewFactory
{
    /// <summary>
    /// 创建工具表面。只在 UI 线程调用；抛异常时宿主会记日志并回退默认工具台。
    /// </summary>
    Control? CreateView(IToolViewContext context);
}
