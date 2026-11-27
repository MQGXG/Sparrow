namespace Sparrow.App.Services.LanShare;

/// <summary>
/// LocalSend 协议实现使用的日志级别。
///
/// 原实现来自 Lertaro.Core.Logger；搬到 Sparrow 后只保留宿主真正需要的四级，
/// 由 <see cref="LanShareLog"/> 转接到 <see cref="App.Log"/>。
/// </summary>
internal enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// LocalSend 的日志与数据目录适配。
///
/// 这是搬迁时新增的桥接层：协议代码仍然调用 Logger 风格 API，
/// 但输出统一进 Sparrow 的日志文件，证书/状态文件也落到 Sparrow 的用户数据目录。
/// </summary>
internal static class LanShareLog
{
    /// <summary>局域网互传自己的数据目录（证书等），与宿主其他数据隔离。</summary>
    public static string UserDataDir { get; } = CreateUserDataDir();

    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        switch (level)
        {
            case LogLevel.Debug:
                // 原 Logger 默认级别是 Info，Debug 默认不落盘；这里保持同样的行为
                return;
            case LogLevel.Warn:
                App.Log.Warn(message);
                return;
            case LogLevel.Error:
                App.Log.Error(message);
                return;
            default:
                App.Log.Info(message);
                return;
        }
    }

    private static string CreateUserDataDir()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "LanShare");

        Directory.CreateDirectory(directory);
        return directory;
    }
}
