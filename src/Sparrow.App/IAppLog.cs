namespace Sparrow.App;

/// <summary>
/// 应用日志的最小抽象。
///
/// 为什么现在不直接上 Serilog：M1 的第一块是「插件能加载、能卸载」，
/// 日志只是为了让加载失败可诊断。等加载链路稳定后再换 Serilog 实现，
/// 那时只需要替换 <see cref="FileAppLog"/>，调用方一行不改。
/// </summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}
