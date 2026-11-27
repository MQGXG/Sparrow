namespace Sparrow.Tests;

/// <summary>
/// 测试运行时定位仓库里的构建产物。
/// 路径从测试程序集所在目录反推，不把 Debug / Release 写死：
/// 本地通常是 Debug，CI 用 Release 构建后 --no-build 跑测试。
/// </summary>
internal static class TestRepoPaths
{
    /// <summary>仓库根目录（含 Sparrow.slnx）。</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>当前测试程序集的构建配置（Debug / Release）。</summary>
    public static string Configuration { get; } = FindConfiguration();

    /// <summary>插件输出目录：plugins/&lt;id&gt;/bin/&lt;配置&gt;/&lt;TFM&gt;。</summary>
    public static string PluginOutput(string pluginId, string targetFramework) =>
        Path.Combine(RepoRoot, "plugins", pluginId, "bin", Configuration, targetFramework);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Sparrow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("找不到仓库根目录（Sparrow.slnx）");
    }

    private static string FindConfiguration()
    {
        // 测试输出形如 .../tests/Sparrow.Tests/bin/Release/net10.0-windows.../
        // 从当前目录往上找，父目录名为 bin 的那一层就是配置目录。
        var directory = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        while (directory is not null)
        {
            if (string.Equals(directory.Parent?.Name, "bin", StringComparison.OrdinalIgnoreCase))
                return directory.Name;

            directory = directory.Parent;
        }

        // 兜底：保持旧行为，至少能跑 Debug 构建
        return "Debug";
    }
}
