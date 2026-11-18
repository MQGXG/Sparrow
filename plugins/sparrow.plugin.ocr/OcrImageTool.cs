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
/// </summary>
public sealed class OcrImageTool : ITool
{
    private const string ParameterLayout = "layout";

    private const string LayoutLine = "按行";
    private const string LayoutRaw = "原文";
    private const string LayoutPosition = "按位置";
    private const string LayoutTable = "表格";

    private readonly IOcrWorkerRunner _runner;

    public OcrImageTool() : this(new ProcessOcrWorkerRunner())
    {
    }

    public OcrImageTool(IOcrWorkerRunner runner) => _runner = runner;

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
            OcrParameters.Language,
            OcrParameters.Layout
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
        var workerPath = OcrParameters.WorkerPath(context.Host);

        if (!File.Exists(workerPath))
            return ToolResult.Failed($"OCR worker 不存在：{workerPath}");

        try
        {
            var language = context.GetParameter("language", "ch");

            await using var session = await _runner.StartAsync(workerPath, language, cancellationToken);
            var output = await session.RecognizeAsync(image.Data, cancellationToken);

            var error = OcrOutputComposer.TryReadError(output);
            if (error is not null)
                return ToolResult.Failed("OCR 执行失败", error);

            return ToolResult.Ok(OcrOutputComposer.Compose(output, OcrParameters.ResolveLayout(context)));
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
}
