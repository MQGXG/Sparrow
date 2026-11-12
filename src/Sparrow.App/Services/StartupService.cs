using Microsoft.Win32;

namespace Sparrow.App.Services;

/// <summary>
/// 开机自启：写 HKCU 的 Run 项，不需要管理员权限。
/// 只启动到托盘，不自动打开工作区。
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sparrow";

    private readonly IAppLog _log;

    public StartupService(IAppLog log) => _log = log;

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return false;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public bool Enable()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
                return false;

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(ValueName, $"\"{executable}\"");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("启用开机自启失败", ex);
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("关闭开机自启失败", ex);
            return false;
        }
    }
}
