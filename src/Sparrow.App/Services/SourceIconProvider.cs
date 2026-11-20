using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace Sparrow.App.Services;

/// <summary>
/// 进程名 → 应用图标（PNG 字节）。
///
/// 历史条目只存进程名——<see cref="Shell.ClipboardMonitor"/> 里写明「只记录进程名，
/// 不记录路径或窗口标题」，这是隐私设计，不能为了图标把路径存进库。
/// 所以图标只能运行时反查：进程名 → 活着的进程 → exe → 关联图标。
/// 来源进程已经退出的历史条目拿不到，返回 null，由调用方回退到首字母色块。
///
/// 结果（含失败）都进缓存：失败也缓存是为了不让列表滚动时反复枚举进程。
/// </summary>
public static class SourceIconProvider
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, bool> Failed =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取来源应用图标的 PNG 字节；拿不到返回 null。</summary>
    public static byte[]? TryGet(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        var name = processName.Trim();

        if (Failed.ContainsKey(name))
            return null;

        if (Cache.TryGetValue(name, out var cached))
            return cached;

        var png = Resolve(name);
        if (png is null)
        {
            // 失败也记一笔，避免每次绑定都重新枚举进程
            Failed[name] = true;
            return null;
        }

        Cache[name] = png;

        // 进程名通常只有几十个，但同样给个上限兜底，避免长期运行只增不减
        if (Cache.Count > MaxCacheEntries)
            Cache.Clear();

        return png;
    }

    /// <summary>进程名缓存条数上限。</summary>
    private const int MaxCacheEntries = 256;

    private static byte[]? Resolve(string processName)
    {
        try
        {
            var path = FindExecutable(processName);
            if (path is null)
                return null;

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
                return null;

            using var source = icon.ToBitmap();
            using var stream = new MemoryStream();
            source.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            // 图标提取失败不能影响列表渲染
            return null;
        }
    }

    /// <summary>同名进程可能有多个，取第一个拿得到路径的。</summary>
    private static string? FindExecutable(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return null;
        }

        foreach (var process in processes)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
            catch
            {
                // 权限不足 / 已退出 / 32-64 位差异：换下一个
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }
}
