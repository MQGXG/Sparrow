using System.Diagnostics;
using System.IO;

namespace Sparrow.App.Services;

/// <summary>
/// 选区诊断日志。每次截图重置，写到 %LOCALAPPDATA%\Sparrow\selector.log。
/// 只用于定位「为什么只选到窗口」，发布前可以整体去掉。
/// </summary>
public static class SelectorTrace
{
    private static readonly object Gate = new();

    /// <summary>
    /// 选区诊断只在调试时开启：附加调试器或显式设置 SPARROW_SELECTOR_DIAGNOSTICS=1。
    /// 正常运行时完全不写盘，避免每次鼠标移动都产生文件 I/O。
    /// </summary>
    private static readonly bool Enabled = Debugger.IsAttached
        || Environment.GetEnvironmentVariable("SPARROW_SELECTOR_DIAGNOSTICS") == "1";

    private static readonly string DirectoryPath = SparrowPaths.Logs;
    private static readonly string FilePath = Path.Combine(DirectoryPath, "selector.log");
    private static readonly string PreviousPath = Path.Combine(DirectoryPath, "selector.prev.log");

    public static void Reset()
    {
        if (!Enabled)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);

            // 上一次截图的日志先归档：否则下一次 Reset 会把现场覆盖掉，问题发生时已经无从查证。
            if (File.Exists(FilePath))
            {
                try
                {
                    File.Copy(FilePath, PreviousPath, overwrite: true);
                }
                catch
                {
                    // 归档失败不影响本次日志
                }
            }

            File.WriteAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} reset " +
                                        $"debugger={Debugger.IsAttached} build={BuildFlavor}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不影响截图
        }
    }

    public static void Write(string message)
    {
        if (!Enabled)
            return;

        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不影响截图
        }
    }

#if DEBUG
    private const string BuildFlavor = "DEBUG";
#else
    private const string BuildFlavor = "RELEASE";
#endif
}
