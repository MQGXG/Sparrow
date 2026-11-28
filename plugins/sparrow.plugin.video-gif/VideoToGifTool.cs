using System.Diagnostics;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Video.Gif;

/// <summary>视频转 GIF：palettegen/paletteuse 两遍调色板。</summary>
public sealed class VideoToGifTool : ITool
{
    private const string ParameterFps = "fps";
    private const string ParameterWidth = "width";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.video.toGif",
        Name = "视频转 GIF",
        Category = "video",
        Description = "把视频转成 GIF，可调帧率与最长边",
        Keywords = ["video", "gif", "动图", "表情包", "转换"],
        Accepts = InputShape.Files,
        Produces = OutputShape.FileSet,
        Order = 50,
        IsHeavy = true,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterFps,
                Label = "帧率",
                Kind = ParameterKind.Choice,
                Choices = ["10", "15", "20"],
                Default = "15"
            },
            new ToolParameter
            {
                Key = ParameterWidth,
                Label = "最长边",
                Kind = ParameterKind.Choice,
                Choices = ["480", "720", "1080"],
                Default = "720"
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

        var fps = context.GetParameter(ParameterFps, "15");
        var width = context.GetParameter(ParameterWidth, "720");

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "sparrow-video",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outputDirectory);

        var filter =
            $"fps={fps},scale='min({width},iw)':-1:flags=lanczos," +
            "split[s0][s1];[s0]palettegen=stats_mode=diff[p];" +
            "[s1][p]paletteuse=dither=bayer:bayer_scale=5";

        var outputs = new List<string>(files.Paths.Count);

        foreach (var input in files.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(input))
                return ToolResult.InvalidInput($"文件不存在：{Path.GetFileName(input)}");

            var output = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(input) + ".gif");

            var arguments =
                "-hide_banner -loglevel error " +
                $"-i \"{input}\" -vf \"{filter}\" -loop 0 -y \"{output}\"";

            var (exitCode, error) = await ProcessRunner.RunAsync(ffmpeg, arguments, cancellationToken);

            if (exitCode != 0)
                return ToolResult.Failed($"转 GIF 失败：{Path.GetFileName(input)}", error);

            outputs.Add(output);
        }

        return ToolResult.Ok(new FileSetPayload(outputs));
    }

    private static string? FindFfmpeg() => ProcessRunner.FindFfmpeg();
}

/// <summary>插件内部的 ffmpeg 定位与执行（四个视频插件共用同一套约定）。</summary>
internal static class ProcessRunner
{
    public static string? FindFfmpeg()
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

    public static async Task<(int ExitCode, string Error)> RunAsync(
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
