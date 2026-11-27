using System.Runtime.InteropServices;

namespace Sparrow.App.Services.LanShare;

/// <summary>
/// LocalSend 的路径解析。
///
/// 设置里的接收目录允许两种写法：
/// - 普通路径，支持 <c>%USERPROFILE%</c> 这类环境变量；
/// - Windows Shell 令牌，例如 <c>shell:Downloads</c>。
///
/// 搬到 Sparrow 后替换掉原来的 Lertaro.PluginSdk.Helpers.UserPathResolver，
/// 只保留 LocalSend 实际用到的这几个已知文件夹。
/// </summary>
internal static class LanSharePaths
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>把设置里的目录解析成真实文件系统路径。</summary>
    public static string Resolve(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return GetKnownFolder(DownloadsFolderId);

        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());

        if (!expanded.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return expanded;

        return expanded.ToLowerInvariant() switch
        {
            "shell:downloads" => GetKnownFolder(DownloadsFolderId),
            "shell:desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "shell:documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "shell:pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "shell:music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "shell:videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "shell:profile" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            // 认不出的令牌原样返回，交给调用方报错，避免猜错目录
            _ => expanded
        };
    }

    private static string GetKnownFolder(Guid folderId)
    {
        try
        {
            var result = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var path);

            if (result == 0 && path != IntPtr.Zero)
            {
                try
                {
                    return Marshal.PtrToStringUni(path)!;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(path);
                }
            }
        }
        catch
        {
            // 已知文件夹 API 不可用时退回用户目录下的 Downloads
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint flags,
        IntPtr token,
        out IntPtr path);
}
