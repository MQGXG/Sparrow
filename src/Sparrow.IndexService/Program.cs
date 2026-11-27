using System.Diagnostics;
using System.ServiceProcess;
using Lertaro.Core;
using Lertaro.Core.Indexer.Usn;

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

        var setDrives = GetStringArg(args, "--set-drives");
        if (!string.IsNullOrWhiteSpace(setDrives))
            return SetDrives(setDrives);

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

    /// <summary>
    /// 管理员入口：写入机器级索引盘配置，删除未选盘缓存，然后重启服务让配置生效。
    /// 不走管道，因为服务的 SetMachineSettings 只认 Lertaro.App.exe（Sparrow 进程名不同）。
    /// </summary>
    private static int SetDrives(string value)
    {
        var drives = value
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(drive => drive.TrimEnd('\\', ':', '/').ToUpperInvariant())
            .Where(drive => drive.Length == 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var volumeIds = drives
            .Select(VolumeHelper.GetVolumeId)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var settings = new MachineSettings
        {
            LocalDrives = volumeIds,
            LocalDriveSelectionConfigured = true
        };

        settings.Save();

        // 先停服务，否则 .idx 还被 mmap 占用，删除会失败
        Run("sc.exe", "stop SparrowIndexService");

        try
        {
            using var controller = new ServiceController("SparrowIndexService");
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        }
        catch
        {
            // 服务可能本来就没有运行
        }

        // 取消的盘：删掉索引缓存，释放磁盘和后续加载
        var cacheDir = Path.Combine(Logger.SharedDataDir, "indexes");

        try
        {
            foreach (var cached in LocalDriveCacheLocator.ListCachedDrives(cacheDir))
            {
                if (!drives.Contains(cached.Drive, StringComparer.OrdinalIgnoreCase))
                    LocalDriveCacheLocator.Delete(cacheDir, cached.Drive);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"清理未选盘索引失败：{ex.Message}");
        }

        // 重启服务，让服务按新的盘列表重新加载/构建
        return Run("sc.exe", "start SparrowIndexService");
    }

    private static string? GetStringArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

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
