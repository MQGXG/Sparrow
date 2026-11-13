using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Ocr;

/// <summary>
/// OCR 工具（C 档：进程插件）。
///
/// 宿主看到的仍然只是一个 <see cref="ITool"/>；「用子进程」是这个插件自己的实现细节。
/// 这么做是因为 Paddle + OpenCV 体积大、无 arm64，而且原生崩溃会带走宿主——
/// 进程隔离是唯一可靠的解法。
///
/// M1 的 worker 是假的：只验证 IPC。换成真模型时，协议与宿主侧代码都不用改。
/// </summary>
public sealed class OcrImageTool : ITool
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(30);

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.ocr.image",
        Name = "图片文字识别",
        Category = "ocr",
        Description = "识别剪贴板图片里的文字",
        Keywords = ["ocr", "识别", "文字", "图片", "提取"],
        Accepts = InputShape.Image,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.Ocr,
        Order = 30,
        Parameters =
        [
            new ToolParameter
            {
                Key = "language",
                Label = "语言",
                Kind = ParameterKind.Choice,
                Choices = ["ch", "en", "ja", "ko", "ru", "uk", "ar", "fa", "bg", "sr", "hi", "th", "el", "fr", "de", "es"],
                Default = "ch",
                Hint = "ch 包含简体、繁体、拼音、英日；ru 覆盖俄语/乌克兰/白俄"
            }
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not ImageInput image)
            return ToolResult.InvalidInput("需要图片输入");

        if (image.ByteSize == 0)
            return ToolResult.InvalidInput("图片为空");

        if (context.Host is null)
            return ToolResult.Failed("宿主未注入能力，无法定位 OCR worker");

        // 进程插件唯一需要宿主提供的东西：自己在哪
        var workerPath = Path.Combine(
            context.Host.PluginDirectory, "worker", "Sparrow.Ocr.Worker.exe");

        if (!File.Exists(workerPath))
            return ToolResult.Failed($"OCR worker 不存在：{workerPath}");

        try
        {
            var language = context.GetParameter("language", "ch");
            var output = await RunWorkerAsync(workerPath, image.Data, language, cancellationToken);
            return ToolResult.Ok(ComposeOutput(output));
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Cancelled();
        }
        catch (Exception ex)
        {
            // 插件内部出错必须转成 ToolResult，不能把异常抛回宿主
            context.Host.Logger.LogError(ex, "OCR worker 执行失败");
            return ToolResult.Failed("OCR 执行失败", ex.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// worker 返回结构化 JSON；按坐标重排文本块，并把行框一起交给宿主。
    /// 兼容旧版纯文本输出（不以 { 开头时原样返回，行框为空）。
    /// </summary>
    private static OcrResultPayload ComposeOutput(string output)
    {
        var trimmed = output.Trim();

        if (!trimmed.StartsWith('{'))
            return new OcrResultPayload(trimmed, Array.Empty<OcrTextLine>());

        try
        {
            var payload = JsonSerializer.Deserialize<OcrWorkerResult>(trimmed, JsonOptions);
            if (payload is null)
                return new OcrResultPayload(trimmed, Array.Empty<OcrTextLine>());

            var lines = OcrTextComposer.ComposeLines(payload.Regions);
            var text = string.Join(Environment.NewLine, lines.Select(line => line.Text));

            // 坐标拼不出来时回退到 worker 的原始全文
            if (string.IsNullOrWhiteSpace(text))
                text = payload.Text;

            return new OcrResultPayload(text, lines);
        }
        catch (JsonException)
        {
            return new OcrResultPayload(trimmed, Array.Empty<OcrTextLine>());
        }
    }

    private static async Task<string> RunWorkerAsync(
        string workerPath,
        ReadOnlyMemory<byte> image,
        string language,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WorkerTimeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--language");
        startInfo.ArgumentList.Add(language);

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 OCR worker");

        // 协议：4 字节小端长度 + 图片字节
        var header = BitConverter.GetBytes(image.Length);
        await process.StandardInput.BaseStream.WriteAsync(header, timeout.Token);
        await process.StandardInput.BaseStream.WriteAsync(image, timeout.Token);
        await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

        await process.WaitForExitAsync(timeout.Token);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"worker 退出码 {process.ExitCode}：{error.Trim()}");

        return output.Trim();
    }
}
