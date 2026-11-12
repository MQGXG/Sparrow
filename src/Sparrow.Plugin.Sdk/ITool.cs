namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 工具的唯一实现接口。
///
/// 只有两个成员，这是刻意的：
/// - 元数据是静态属性 → 命令面板搜索时不需要加载程序集、不需要实例化
/// - 执行是唯一副作用入口 → 调度器只需在一处做异常隔离
///
/// 约束：实现必须无状态、可并发。这是「插件能随时卸载」的前提，
/// 也是「同一工具重复执行结果一致」的前提（见 docs/architecture.md 第四节）。
/// </summary>
public interface ITool
{
    /// <summary>静态元数据。必须是常量，不能依赖运行时初始化。</summary>
    ToolDescriptor Descriptor { get; }

    /// <summary>
    /// 执行工具。
    /// 不得抛异常表达「输入不对」——用 <see cref="ToolResult.InvalidInput"/> 返回。
    /// 不得直接访问剪贴板、文件、数据库、窗口，只能使用 <see cref="ToolContext"/> 提供的输入
    /// 和 <see cref="IToolHost"/> 注入的能力。
    /// </summary>
    Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken);
}
