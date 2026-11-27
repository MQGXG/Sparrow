using System.Diagnostics;
using System.ServiceProcess;
using Lertaro.Core;
using Lertaro.Core.Services;

namespace Sparrow.IndexService;

/// <summary>Windows 服务本体：启动 SearchEngine，由它完成索引与 USN 增量。</summary>
public sealed class IndexService : ServiceBase
{
    private SearchEngine? _engine;
    private UsnServicePipeServer? _pipe;

    public IndexService()
    {
        ServiceName = "SparrowIndexService";
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args) => StartCore();

    protected override void OnStop() => StopCore();

    protected override void OnShutdown()
    {
        StopCore();
        base.OnShutdown();
    }

    internal void StartConsole() => StartCore();

    internal void StopConsole() => StopCore();

    private void StartCore()
    {
        Logger.Initialize("sparrow-index.log", Logger.SharedDataDir, overwrite: true);
        EnsureIndexDirectory();

        _engine = new SearchEngine();
        _engine.InitializeOrLoadIndex(false);

        // 宿主通过命名管道查询服务内存里的实时索引，不直接读 .idx
        _pipe = new UsnServicePipeServer();
        _pipe.Start(_engine, Stop);

        Logger.Log("[Sparrow.IndexService] 索引服务已启动。");
    }

    private void StopCore()
    {
        try
        {
            _pipe?.Stop();
            _pipe?.Dispose();
        }
        catch
        {
            // 管道关闭失败不阻塞服务停止
        }

        _pipe = null;

        try
        {
            _engine?.Dispose();
        }
        catch
        {
            // 服务停止不应该因为索引释放失败而报错
        }

        _engine = null;
        Logger.Log("[Sparrow.IndexService] 索引服务已停止。");
    }

    /// <summary>
    /// 服务以 SYSTEM 写索引，普通用户需要读权限才能 mmap 查询。
    /// 用 icacls 给 Users 组授予读/列目录权限，并继承到子文件。
    /// </summary>
    private static void EnsureIndexDirectory()
    {
        var directory = Path.Combine(Logger.SharedDataDir, "indexes");
        Directory.CreateDirectory(directory);

        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "icacls",
                $"\"{directory}\" /grant *S-1-5-32-545:(OI)(CI)RX /T /C")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"[Sparrow.IndexService] 设置索引目录权限失败：{ex.Message}",
                LogLevel.Warn);
        }
    }
}
