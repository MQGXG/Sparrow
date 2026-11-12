namespace Sparrow.App;

/// <summary>
/// 极简崩溃日志。
/// WinExe 没有控制台，启动期异常必须落盘，否则用户只会看到「双击没反应」。
/// 正式的 Serilog 管道在 M1 接入，这里只保证「出事有迹可循」。
/// </summary>
internal static class CrashLog
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sparrow-crash.log");

    public static void Write(Exception exception)
    {
        try
        {
            var text = $"""
                [{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {exception.GetType().FullName}
                {exception.Message}

                {exception.StackTrace}

                """;
            System.IO.File.AppendAllText(Path, text);
        }
        catch
        {
            // 日志本身失败时不做任何事，避免在崩溃路径上再抛一次
        }
    }
}
