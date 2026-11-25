using System.Diagnostics;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// ffmpeg 子进程编码器：WGC 抓到的 BGRA 帧通过 stdin 管道交给 ffmpeg，
/// 由 libx264 编码成 MP4。
///
/// 为什么不用 Media Foundation：本机（Windows 10 22H2，mfplat 10.0.19041.4522）
/// 的 MFCreateSample 返回对象只支持 IMFAttributes（QueryInterface(IMFSample)
/// 返回 E_NOINTERFACE），软编、硬编、MJPG 编码器全部无法接收样本。
/// ffmpeg 子进程同时符合项目已有的进程先例（OCR worker），
/// 且保留了 WGC 的硬件加速捕获能力——ffmpeg 只负责编码，不负责抓屏。
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly string _path;
    private bool _completed;
    private bool _disposed;

    private FfmpegEncoder(Process process, string path)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _path = path;
    }

    /// <summary>创建编码器；ffmpeg 缺失或启动失败返回 null。</summary>
    public static FfmpegEncoder? TryCreate(
        string path,
        int width,
        int height,
        int framesPerSecond,
        int crf,
        string preset,
        int gopSeconds,
        out string? error)
    {
        error = null;

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            error = "未找到 ffmpeg.exe：请把它放到 tools/ffmpeg/，或加入 PATH。";
            return null;
        }

        // 只接受白名单：参数会进命令行，不能让外部任意文本拼进去。
        var safePreset = preset is "ultrafast" or "superfast" or "veryfast" or "faster" or "medium"
            ? preset
            : "veryfast";

        // -g 是关键帧间隔帧数：秒数 × 帧率。
        var gop = gopSeconds > 0
            ? $" -g {Math.Max(1, framesPerSecond * gopSeconds)}"
            : "";

        var arguments =
            "-hide_banner -loglevel error " +
            $"-f rawvideo -pix_fmt bgra -s {width}x{height} -r {framesPerSecond} -i pipe:0 " +
            $"-c:v libx264 -preset {safePreset} -crf {crf}{gop} " +
            "-pix_fmt yuv420p " +
            // 分片 MP4：进程被强杀时已经写入的分片仍可播放（普通 MP4 会丢 moov）。
            "-movflags +frag_keyframe+empty_moov+default_base_moof -y " +
            $"\"{path}\"";

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, arguments)
                {
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                error = "无法启动 ffmpeg 进程。";
                process.Dispose();
                return null;
            }

            return new FfmpegEncoder(process, path);
        }
        catch (Exception ex)
        {
            error = $"启动 ffmpeg 失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>写一帧 BGRA（top-down，stride = width * 4）。</summary>
    public void WriteFrame(byte[] bgraTopDown)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            _input.Write(bgraTopDown, 0, bgraTopDown.Length);
        }
        catch (IOException ex)
        {
            // 管道断开通常意味着 ffmpeg 已因编码错误退出。
            throw new InvalidOperationException(
                $"ffmpeg 输入管道已关闭：{ReadError()}", ex);
        }
    }

    /// <summary>关闭 stdin，等 ffmpeg 写完文件索引。</summary>
    public void Complete()
    {
        if (_completed)
            return;

        _completed = true;

        try
        {
            _input.Close();

            if (!_process.WaitForExit(10_000))
            {
                _process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("ffmpeg 未在 10 秒内结束，文件可能不完整。");
            }

            if (_process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg 退出码 {_process.ExitCode}：{ReadError()}");
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>放弃当前文件：直接杀掉 ffmpeg。</summary>
    public void Abandon()
    {
        if (_disposed)
            return;

        _disposed = true;
        _completed = true;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已经退出。
        }
        finally
        {
            _process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            Complete();
        }
        catch
        {
            Abandon();
        }
    }

    private string ReadError()
    {
        try
        {
            return _process.StandardError.ReadToEnd().Trim();
        }
        catch
        {
            return "（无法读取 ffmpeg 错误输出）";
        }
    }

    /// <summary>
    /// 把无声视频和 WAV 合并成最终 MP4。
    /// 视频流直接 copy（不再编码），音频转 AAC 192k，-shortest 截齐长度。
    /// </summary>
    public static void Merge(string videoPath, string audioPath, string outputPath)
    {
        var ffmpeg = FindFfmpeg()
                     ?? throw new InvalidOperationException("未找到 ffmpeg.exe。");

        var arguments =
            "-hide_banner -loglevel error " +
            $"-i \"{videoPath}\" -i \"{audioPath}\" " +
            "-c:v copy -c:a aac -b:a 192k -shortest -y " +
            $"\"{outputPath}\"";

        using var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("无法启动 ffmpeg 合并进程。");

        var error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("合并音频超时。");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"合并音频失败：{error.Trim()}");
    }

    /// <summary>
    /// 把录好的 MP4 转成 GIF。
    /// 两遍调色板（palettegen + paletteuse）是 ffmpeg 的标准做法，
    /// 比单遍的直接量化色彩准确得多。
    /// </summary>
    public static void ToGif(string videoPath, string outputPath, int fps, int width)
    {
        var ffmpeg = FindFfmpeg()
                     ?? throw new InvalidOperationException("未找到 ffmpeg.exe。");

        var filter =
            $"fps={fps},scale='min({width},iw)':-1:flags=lanczos," +
            "split[s0][s1];[s0]palettegen=stats_mode=diff[p];" +
            "[s1][p]paletteuse=dither=bayer:bayer_scale=5";

        var arguments =
            "-hide_banner -loglevel error " +
            $"-i \"{videoPath}\" " +
            $"-vf \"{filter}\" " +
            "-loop 0 -y " +
            $"\"{outputPath}\"";

        using var process = Process.Start(new ProcessStartInfo(ffmpeg, arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("无法启动 ffmpeg GIF 转换进程。");

        var error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("生成 GIF 超时。");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"生成 GIF 失败：{error.Trim()}");
    }

    /// <summary>优先用随包分发的 ffmpeg，其次首次下载的本地目录，最后 PATH。</summary>
    private static string? FindFfmpeg() => FfmpegLocator.Find();
}
