using System.Diagnostics;
using Avalonia;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;
using Velopack;

namespace Sparrow.App;

internal static class Program
{
    /// <summary>进程启动时刻。用于冷启动验收（从 Main 第一行开始计时）。</summary>
    internal static Stopwatch Startup { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack 的安装 / 更新钩子：必须是第一行，早于单实例互斥与任何 UI。
        // 开发态（未通过 Setup 安装）调用是空操作。
        VelopackApp.Build().Run();

        Startup = Stopwatch.StartNew();

        // 后台线程 / 未观察任务异常：至少落盘，不让 WinExe 静默退出
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                CrashLog.Write(exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };

        // 置顶看门狗：独立子进程，不建互斥量、不加载 Avalonia。
        // 宿主被强杀时 stdin 关闭，它负责把记录的窗口恢复成非置顶。
        if (args.Any(arg => arg.Equals("--pin-watchdog", StringComparison.OrdinalIgnoreCase)))
            return PinWatchdog.Run(Console.In, new Win32PinWatchdogNative());

        // 单实例：M0 只做「第二个实例直接退出」。
        // 完整做法（把唤起请求转发给第一个实例）留给 M1，避免现在就引入 IPC。
        //
        // 录制自检（SPARROW_RECORD_PROBE）是独立进程行为：必须绕过单实例，
        // 否则正在运行的 Sparrow 会让 probe 直接退出，什么也测不到。
        var isRecordingProbe = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(RecordingProbe.EnvironmentVariable));

        // 提权索引子进程也是独立进程行为：必须绕过单实例，
        // 否则正在运行的 Sparrow 会让它直接退出，索引永远建不起来。
        var isIndexBuild = args.Any(arg =>
            arg.Equals("--build-index", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(initiallyOwned: true, @"Local\Sparrow.SingleInstance", out var isFirst);
        if (!isFirst && !isRecordingProbe && !isIndexBuild)
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
