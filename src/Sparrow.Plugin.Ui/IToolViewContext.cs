namespace Sparrow.Plugin.Ui;

/// <summary>
/// 宿主提供给自定义视图的上下文：参数读写 + 当前输入文件清单。
///
/// 参数最终仍会进入 <c>ToolContext.Parameters</c>，与宿主默认参数区共用同一个存储，
/// 所以「界面里改过的值」和「执行时用到的值」不会分叉。
/// </summary>
public interface IToolViewContext
{
    /// <summary>读取当前工具的一个参数；键不存在时返回 <c>null</c>。</summary>
    string? GetParameter(string key);

    /// <summary>
    /// 写入当前工具的一个参数。键不存在时由宿主补一条覆盖值，
    /// 执行时一起并入 <c>ToolContext.Parameters</c>。
    /// </summary>
    void SetParameter(string key, string? value);

    /// <summary>当前工具已拖入 / 选择的输入文件；每次读取返回当时的一份快照。</summary>
    IReadOnlyList<string> InputFiles { get; }
}
