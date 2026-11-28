using System.Diagnostics;
using Sparrow.App.Overlays;

namespace Sparrow.App.Services;

/// <summary>
/// 置顶看门狗：Sparrow 被强杀时，替它取消还在置顶的窗口。
///
/// 宿主通过 stdin 发命令（pin / unpin / clear）。宿主进程退出——包括任务管理器强杀——
/// 时 stdin 被关闭，看门狗读到 EOF 后对记录里的窗口执行 SetTopMost(false)。
///
/// 只处理「句柄仍然存在，且所属进程没变」的窗口：HWND 会复用，
/// 不能拿一个旧句柄去取消别的窗口的置顶。
/// </summary>
internal static class PinWatchdog
{
    public static int Run(TextReader input, IPinWatchdogNative native)
    {
        var targets = new Dictionary<nint, uint>();

        try
        {
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                switch (parts[0])
                {
                    case "pin" when parts.Length >= 3
                        && long.TryParse(parts[1], out var pinHandle)
                        && uint.TryParse(parts[2], out var pinProcessId):
                        targets[(nint)pinHandle] = pinProcessId;
                        break;

                    case "unpin" when parts.Length >= 2
                        && long.TryParse(parts[1], out var unpinHandle):
                        targets.Remove((nint)unpinHandle);
                        break;

                    case "clear":
                        Restore(targets, native);
                        targets.Clear();
                        break;
                }
            }
        }
        catch (IOException)
        {
            // stdin 被关闭就是退出信号
        }
        catch (ObjectDisposedException)
        {
        }

        Restore(targets, native);
        return 0;
    }

    private static void Restore(Dictionary<nint, uint> targets, IPinWatchdogNative native)
    {
        foreach (var (handle, processId) in targets)
        {
            if (!native.Exists(handle) || native.GetProcessId(handle) != processId)
                continue;

            native.ClearTopMost(handle);
        }
    }
}

/// <summary>看门狗需要的 Win32 操作；测试用假实现。</summary>
internal interface IPinWatchdogNative
{
    bool Exists(nint handle);

    uint GetProcessId(nint handle);

    void ClearTopMost(nint handle);
}

internal sealed class Win32PinWatchdogNative : IPinWatchdogNative
{
    public bool Exists(nint handle) => WindowPickerApi.Exists(handle);

    public uint GetProcessId(nint handle) => WindowPickerApi.GetProcessId(handle);

    public void ClearTopMost(nint handle) => WindowPickerApi.SetTopMost(handle, false);
}

/// <summary>宿主侧：启动并驱动看门狗。启动失败只记日志，不影响置顶本身。</summary>
internal sealed class PinWatchdogClient : IDisposable
{
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private Process? _process;
    private bool _disposed;

    public PinWatchdogClient(IAppLog log) => _log = log;

    public void Pin(nint handle)
    {
        var processId = WindowPickerApi.GetProcessId(handle);
        Write($"pin {(long)handle} {processId}");
    }

    public void Unpin(nint handle) => Write($"unpin {(long)handle}");

    public void Clear() => Write("clear");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            Stop();
        }
    }

    private void Write(string command)
    {
        lock (_gate)
        {
            if (_disposed || !EnsureStarted())
                return;

            try
            {
                _process!.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                // 看门狗挂了不该影响正常置顶：下一次 Pin 会重新拉起
                _log.Warn($"置顶看门狗写入失败：{ex.Message}");
                Stop();
            }
        }
    }

    private bool EnsureStarted()
    {
        if (_process is { HasExited: false })
            return true;

        var executable = Environment.ProcessPath;
        if (executable is null
            || !string.Equals(
                Path.GetFileNameWithoutExtension(executable),
                "Sparrow.App",
                StringComparison.OrdinalIgnoreCase))
        {
            // 测试宿主 / 非 App 入口：不启动看门狗
            return false;
        }

        try
        {
            _process = Process.Start(new ProcessStartInfo(executable, "--pin-watchdog")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            });

            return _process is not null;
        }
        catch (Exception ex)
        {
            _log.Warn($"置顶看门狗启动失败：{ex.Message}");
            return false;
        }
    }

    private void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.StandardInput.WriteLine("clear");
                _process.StandardInput.Flush();
                _process.StandardInput.Close();
            }
        }
        catch
        {
            // 进程已经退出 / 管道已断，忽略
        }

        _process?.Dispose();
        _process = null;
    }
}
