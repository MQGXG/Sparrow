using System.Diagnostics;
using Avalonia.Controls;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Ui;

namespace Sparrow.Plugin.Video.Trim;

/// <summary>
/// 视频裁剪：输入一组视频，按起止秒数截取片段。
///
/// 编码交给随宿主的 ffmpeg（与录屏共用同一个二进制），
/// 插件只负责参数校验、定位文件和返回 FileSetPayload。
/// 自定义表面只做起止区间选择，执行逻辑与无头执行完全一致。
/// </summary>
public sealed class VideoTrimTool : ITool, IToolViewFactory
{
    private const string ParameterStart = "start";
    private const string ParameterEnd = "end";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.video.trim",
        Name = "视频裁剪",
        Category = "video",
        Description = "按起止时间裁剪视频，支持多文件",
        Keywords = ["video", "trim", "裁剪", "视频", "截取", "片段"],
        Accepts = InputShape.Files,
        Produces = OutputShape.FileSet,
        Order = 52,
        IsHeavy = true,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterStart,
                Label = "开始（秒）",
                Kind = ParameterKind.Text,
                Default = "0",
                Hint = "从不含关键帧的中间开始可能黑屏；需要精确请用重编码模式"
            },
            new ToolParameter
            {
                Key = ParameterEnd,
                Label = "结束（秒）",
                Kind = ParameterKind.Text,
                Default = "10",
                Hint = "必须大于开始时间"
            }
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not FileSetInput files || files.Paths.Count == 0)
            return ToolResult.InvalidInput("需要视频文件");

        var startText = context.GetParameter(ParameterStart, "0");
        var endText = context.GetParameter(ParameterEnd, "10");

        if (!double.TryParse(startText, out var start) || start < 0)
            return ToolResult.InvalidInput("开始时间必须是大于等于 0 的秒数");

        if (!double.TryParse(endText, out var end) || end <= start)
            return ToolResult.InvalidInput("结束时间必须大于开始时间");

        var ffmpeg = FindFfmpeg();

        if (ffmpeg is null)
            return ToolResult.Unsupported("未找到 ffmpeg.exe：请把它放到 tools/ffmpeg/");

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

            var name = Path.GetFileNameWithoutExtension(input) + "-trim" + Path.GetExtension(input);
            var output = Path.Combine(outputDirectory, name);

            var arguments =
                "-hide_banner -loglevel error " +
                $"-ss {start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"-to {end.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"-i \"{input}\" -c copy -y \"{output}\"";

            var (exitCode, error) = await RunAsync(ffmpeg, arguments, cancellationToken);

            if (exitCode != 0)
                return ToolResult.Failed(
                    $"裁剪失败：{Path.GetFileName(input)}",
                    error);

            outputs.Add(output);
        }

        return ToolResult.Ok(new FileSetPayload(outputs));
    }

    /// <summary>只在 UI 线程由宿主调用；时间轴写回的就是执行时读取的同一组参数。</summary>
    public Control? CreateView(IToolViewContext context) => new VideoTrimView(context);

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
