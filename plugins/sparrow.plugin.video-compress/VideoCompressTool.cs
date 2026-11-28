using System.Diagnostics;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Video.Compress;

/// <summary>
/// 视频压缩：按质量档位和分辨率重编码。
///
/// 编码交给随宿主的 ffmpeg；插件不下载、不打包任何二进制。
/// </summary>
public sealed class VideoCompressTool : ITool
{
    private const string ParameterQuality = "quality";
    private const string ParameterScale = "scale";

    private const string QualityLow = "low";
    private const string QualityBalanced = "balanced";
    private const string QualityHigh = "high";

    private const string ScaleOriginal = "original";
    private const string Scale1080 = "1080";
    private const string Scale720 = "720";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.video.compress",
        Name = "视频压缩",
        Category = "video",
        Description = "按质量与分辨率重新编码视频，减小体积",
        Keywords = ["video", "compress", "压缩", "视频", "瘦身", "小一点"],
        Accepts = InputShape.Files,
        Produces = OutputShape.FileSet,
        Order = 53,
        IsHeavy = true,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterQuality,
                Label = "质量",
                Kind = ParameterKind.Choice,
                Choices = [QualityLow, QualityBalanced, QualityHigh],
                Default = QualityBalanced,
                ChoiceLabels = new Dictionary<string, string>
                {
                    [QualityLow] = "低（更小）",
                    [QualityBalanced] = "均衡",
                    [QualityHigh] = "高（更清晰）"
                }
            },
            new ToolParameter
            {
                Key = ParameterScale,
                Label = "分辨率",
                Kind = ParameterKind.Choice,
                Choices = [ScaleOriginal, Scale1080, Scale720],
                Default = ScaleOriginal,
                ChoiceLabels = new Dictionary<string, string>
                {
                    [ScaleOriginal] = "原始尺寸",
                    [Scale1080] = "1080p",
                    [Scale720] = "720p"
                }
            }
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not FileSetInput files || files.Paths.Count == 0)
            return ToolResult.InvalidInput("需要视频文件");

        var ffmpeg = FindFfmpeg();

        if (ffmpeg is null)
            return ToolResult.Unsupported("未找到 ffmpeg.exe：请把它放到 tools/ffmpeg/");

        var crf = context.GetParameter(ParameterQuality, QualityBalanced) switch
        {
            QualityLow => "30",
            QualityHigh => "19",
            _ => "24"
        };

        var scale = context.GetParameter(ParameterScale, ScaleOriginal) switch
        {
            Scale1080 => "-vf scale=-2:1080",
            Scale720 => "-vf scale=-2:720",
            _ => ""
        };

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "sparrow-video",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outputDirectory);

        var outputs = new List<string>(files.Paths.Count);

        foreach (var input in files.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(input))
                return ToolResult.InvalidInput($"文件不存在：{Path.GetFileName(input)}");

            var name = Path.GetFileNameWithoutExtension(input) + "-compressed" + Path.GetExtension(input);
            var output = Path.Combine(outputDirectory, name);

            var arguments =
                "-hide_banner -loglevel error " +
                $"-i \"{input}\" " +
                $"-c:v libx264 -preset veryfast -crf {crf} " +
                (string.IsNullOrEmpty(scale) ? "" : $"{scale} ") +
                "-c:a aac -b:a 128k " +
                $"-y \"{output}\"";

            var (exitCode, error) = await RunAsync(ffmpeg, arguments, cancellationToken);

            if (exitCode != 0)
                return ToolResult.Failed(
                    $"压缩失败：{Path.GetFileName(input)}",
                    error);

            outputs.Add(output);
        }

        return ToolResult.Ok(new FileSetPayload(outputs));
    }

    private static string? FindFfmpeg()
    {
        var bundled = Path.Combine(
            AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe");

        if (File.Exists(bundled))
            return bundled;

        // 宿主首次录制时会把 ffmpeg 下载到这里；插件与宿主共用同一份。
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sparrow", "ffmpeg", "ffmpeg.exe");

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
                // 非法 PATH 项跳过。
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(
        string ffmpeg,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpeg, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, error.Trim());
    }
}
