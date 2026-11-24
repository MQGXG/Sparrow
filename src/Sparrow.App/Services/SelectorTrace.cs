using System.IO;

namespace Sparrow.App.Services;

/// <summary>
/// 选区诊断日志。每次截图重置，写到 %LOCALAPPDATA%\Sparrow\selector.log。
/// 只用于定位「为什么只选到窗口」，发布前可以整体去掉。
/// </summary>
public static class SelectorTrace
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sparrow",
        "selector.log");

    public static void Reset()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} reset{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不影响截图
        }
    }

    public static void Write(string message)
    {
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
}
