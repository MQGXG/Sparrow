using System.Diagnostics;
using System.Globalization;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Video.Frames;

/// <summary>视频抽帧：每 N 秒导出一张 PNG。</summary>
public sealed class VideoFramesTool : ITool
{
    private const string ParameterInterval = "interval";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.video.frames",
        Name = "视频抽帧",
        Category = "video",
        Description = "按固定间隔从视频导出 PNG 帧",
        Keywords = ["video", "frame", "抽帧", "截图", "导图"],
        Accepts = InputShape.Files,
        Produces = OutputShape.FileSet,
        Order = 51,
        IsHeavy = true,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterInterval,
                Label = "间隔（秒）",
                Kind = ParameterKind.Choice,
                Choices = ["0.5", "1", "2", "5"],
                Default = "1"
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

        var intervalText = context.GetParameter(ParameterInterval, "1");

        if (!double.TryParse(
                intervalText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var interval)
            || interval <= 0)
        {
            return ToolResult.InvalidInput("间隔必须是大于 0 的秒数");
        }

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "sparrow-video",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outputDirectory);

        var outputs = new List<string>();

        foreach (var input in files.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(input))
                return ToolResult.InvalidInput($"文件不存在：{Path.GetFileName(input)}");

            var name = Path.GetFileNameWithoutExtension(input);
            var pattern = Path.Combine(outputDirectory, $"{name}-%03d.png");

            var arguments =
                "-hide_banner -loglevel error " +
                $"-i \"{input}\" " +
                $"-vf fps=1/{interval.ToString(CultureInfo.InvariantCulture)} " +
                $"-y \"{pattern}\"";

            var (exitCode, error) = await RunAsync(ffmpeg, arguments, cancellationToken);

            if (exitCode != 0)
                return ToolResult.Failed($"抽帧失败：{Path.GetFileName(input)}", error);

            outputs.AddRange(Directory.GetFiles(outputDirectory, $"{name}-*.png"));
        }

        if (outputs.Count == 0)
            return ToolResult.Failed("没有生成任何帧（视频可能太短或无法解码）。");

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
