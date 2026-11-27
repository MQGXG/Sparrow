using System.Diagnostics;
using System.ServiceProcess;
using Lertaro.Core;

namespace Sparrow.IndexService;

/// <summary>
/// Sparrow 的本机文件索引服务。
///
/// 服务以 LocalSystem 运行，拥有读取卷句柄的权限；它复用 Lertaro 的
/// SearchEngine 完成 MFT 全量扫描、USN 增量监听和索引 compact。
/// 宿主只 mmap 读取服务写出的 .idx，不需要自己提权。
/// </summary>
internal static class Program
{
    private const string ServiceName = "SparrowIndexService";
    private const string DisplayName = "Sparrow Index Service";

    private static int Main(string[] args)
    {
        if (HasFlag(args, "--install"))
            return Install();

        if (HasFlag(args, "--uninstall"))
            return Uninstall();

        var probeSeconds = GetIntArg(args, "--probe");
        if (probeSeconds > 0)
        {
            var probe = new IndexService();
            probe.StartConsole();
            Thread.Sleep(probeSeconds * 1000);
            probe.StopConsole();
            return 0;
        }

        if (HasFlag(args, "--console"))
        {
            var service = new IndexService();
            service.StartConsole();
            Console.WriteLine("Sparrow 索引服务已启动（控制台模式），按 Enter 退出。");
            Console.ReadLine();
            service.StopConsole();
            return 0;
        }

        ServiceBase.Run(new IndexService());
        return 0;
    }

    private static int Install()
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            Console.Error.WriteLine("无法确定服务 exe 路径。");
            return 1;
        }

        var binPath = $"\\\"{exe}\\\" --service";

        var create = Run(
            "sc.exe",
            $"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");

        Run(
            "sc.exe",
            $"description {ServiceName} \"Sparrow 本机文件索引与 USN 增量维护\"");

        var start = Run("sc.exe", $"start {ServiceName}");

        return create == 0 && start == 0 ? 0 : 1;
    }

    private static int Uninstall()
    {
        Run("sc.exe", $"stop {ServiceName}");
        return Run("sc.exe", $"delete {ServiceName}");
    }

    private static int Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
                return 1;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Console.WriteLine($"{file} {arguments}: exit={process.ExitCode}");

            if (!string.IsNullOrWhiteSpace(output))
                Console.WriteLine(output.Trim());

            if (!string.IsNullOrWhiteSpace(error))
                Console.Error.WriteLine(error.Trim());

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{file} {arguments} 失败：{ex.Message}");
            return 1;
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static int GetIntArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var value))
            {
                return value;
            }
        }

        return 0;
    }
}
