using Avalonia;

namespace Sparrow.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 单实例：M0 只做「第二个实例直接退出」。
        // 完整做法（把唤起请求转发给第一个实例）留给 M1，避免现在就引入 IPC。
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Sparrow.SingleInstance", out var isFirst);
        if (!isFirst)
            return 0;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // WinExe 没有控制台，异常必须落盘，否则启动失败时无从排查
            CrashLog.Write(ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
