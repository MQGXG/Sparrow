using System.IO.Compression;
using System.Net.Http;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 定位 FFmpeg：随包目录 → 首次自动下载的本地目录 → PATH。
///
/// 仓库不提交 ffmpeg 二进制（约 45MB，且是 GPL 组件）。第一次录制时
/// 自动下载一份 BtbN 的 win64 GPL 构建（含 libx264），解压到
/// %LOCALAPPDATA%\Sparrow\ffmpeg；也可以继续手动放到 tools/ffmpeg/。
/// </summary>
internal static class FfmpegLocator
{
    /// <summary>BtbN 的 latest 链接，永远指向最新的 win64 GPL 构建（含 libx264）。</summary>
    public const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    public static string LocalDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sparrow",
        "ffmpeg");

    /// <summary>查找可用的 ffmpeg.exe；找不到返回 null。</summary>
    public static string? Find()
    {
        var bundled = Path.Combine(
            AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");

        if (File.Exists(bundled))
            return bundled;

        var local = Path.Combine(LocalDirectory, "ffmpeg.exe");

        if (File.Exists(local))
            return local;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");

                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // 非法 PATH 项直接跳过。
            }
        }

        return null;
    }

    /// <summary>
    /// 确保 ffmpeg 可用：已存在直接返回；缺失时下载并解压，返回 ffmpeg.exe 路径。
    /// 下载失败会抛出异常，由调用方提示用户手动放置。
    /// </summary>
    public static async Task<string?> EnsureAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var existing = Find();

        if (existing is not null)
            return existing;

        Directory.CreateDirectory(LocalDirectory);

        var zip = Path.Combine(
            Path.GetTempPath(), $"sparrow-ffmpeg-{Guid.NewGuid():N}.zip");

        try
        {
            await DownloadAsync(zip, progress, cancellationToken);

            progress?.Report(-2); // 解压阶段
            await Task.Run(() => Extract(zip), cancellationToken);

            var ffmpeg = Path.Combine(LocalDirectory, "ffmpeg.exe");

            if (!File.Exists(ffmpeg))
                throw new InvalidOperationException("下载包里没有 ffmpeg.exe。");

            return ffmpeg;
        }
        finally
        {
            try
            {
                if (File.Exists(zip))
                    File.Delete(zip);
            }
            catch
            {
                // 临时文件删不掉不影响使用。
            }
        }
    }

    private static async Task DownloadAsync(
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var response = await http.GetAsync(
            DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);

        var buffer = new byte[81920];
        long received = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);

            if (read <= 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            if (total > 0)
                progress?.Report(Math.Clamp((double)received / total, 0, 1));
            else
                progress?.Report(-1); // 进度未知：只显示已下载字节数
        }
    }

    private static void Extract(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName);

            if (string.IsNullOrEmpty(name))
                continue;

            // 只取 bin/ 下的 exe / dll，外加许可证文本。
            var isBinary = name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

            var isLicense = name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);

            if (!isBinary && !isLicense)
                continue;

            var target = Path.Combine(
                LocalDirectory, isLicense ? "LICENSE-FFmpeg.txt" : name);

            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
