using System.Text;

namespace Sparrow.App;

/// <summary>
/// 写入 <c>%LOCALAPPDATA%\Sparrow\logs\sparrow-yyyyMMdd.log</c> 的简单日志。
/// 线程安全（插件加载可能来自后台线程），单行写入失败不影响主流程。
/// </summary>
public sealed class FileAppLog : IAppLog
{
    private static readonly object Gate = new();

    private readonly string _path;

    public FileAppLog()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow",
            "logs");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"sparrow-{DateTime.Now:yyyyMMdd}.log");
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var builder = new StringBuilder()
            .Append('[').Append(DateTimeOffset.Now.ToString("HH:mm:ss.fff")).Append("] ")
            .Append(level).Append(' ').Append(message);

        if (exception is not null)
            builder.AppendLine().Append(exception);

        try
        {
            lock (Gate)
                File.AppendAllText(_path, builder.AppendLine().ToString());
        }
        catch
        {
            // 日志失败不影响业务，也不在崩溃路径上再抛异常
        }
    }
}
