using System.Text;
using Microsoft.Extensions.Logging;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Ocr;

/// <summary>
/// OCR 工具（C 档：进程插件）。
///
/// 输入是 1..N 张图片：剪贴板单图、拖入多图、文件选择都归一到这里。
/// 宿主看到的仍然只是一个 <see cref="ITool"/>；「用子进程」是这个插件自己的实现细节。
/// 这么做是因为 Paddle + OpenCV 体积大、无 arm64，而且原生崩溃会带走宿主——
/// 进程隔离是唯一可靠的解法。
///
/// 多张图复用同一个 worker 会话：模型只初始化一次，
/// 否则「批量」会被每张图 1-3 秒的初始化淹没。
/// </summary>
public sealed class OcrImageTool : ITool
{
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
        Description = "识别剪贴板、拖入或选择的多张图片文字",
        Keywords = ["ocr", "识别", "文字", "图片", "批量", "多图", "提取"],
        Accepts = InputShape.Images,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.Ocr,
        Order = 30,
        Parameters =
        [
            OcrParameters.Language,
            OcrParameters.Layout,
            OcrParameters.Translate
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not ImageSetInput set)
            return ToolResult.InvalidInput("需要图片输入");

        if (set.Images.Count == 0)
            return ToolResult.InvalidInput("没有可识别的图片");

        if (context.Host is null)
            return ToolResult.Failed("宿主未注入能力，无法定位 OCR worker");

        // 进程插件唯一需要宿主提供的东西：自己在哪
        var workerPath = OcrParameters.WorkerPath(context.Host);

        if (!File.Exists(workerPath))
            return ToolResult.Failed($"OCR worker 不存在：{workerPath}");

        var language = context.GetParameter(OcrParameters.LanguageKey, "ch");
        var layout = OcrParameters.ResolveLayout(context);
        var translateTo = context.GetParameter(OcrParameters.TranslateKey, OcrParameters.TranslateNone);

        var sections = new List<(string Name, string Text)>();
        var lines = new List<OcrTextLine>();

        try
        {
            await using var session = await _runner.StartAsync(workerPath, language, cancellationToken);

            for (var index = 0; index < set.Images.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var image = set.Images[index];
                var name = DisplayName(image, index);

                context.Progress?.Report(new ToolProgress(
                    index / (double)set.Images.Count,
                    $"正在识别 {index + 1}/{set.Images.Count}：{name}"));

                if (image.ByteSize == 0)
                {
                    sections.Add((name, string.Empty));
                    continue;
                }

                try
                {
                    var output = await session.RecognizeAsync(image.Data, cancellationToken);

                    var error = OcrOutputComposer.TryReadError(output);
                    if (error is not null)
                    {
                        context.Host.Logger.LogWarning("OCR：{Name} 失败：{Error}", name, error);
                        sections.Add((name, string.Empty));
                        continue;
                    }

                    var payload = OcrOutputComposer.Compose(output, layout);

                    // 多图时标题带序号和尺寸，「这段字是哪张图」一眼能对上；
                    // 选了翻译就把原文和译文对照着放进同一段
                    var sectionText = await MaybeTranslateAsync(context, payload.Text, translateTo, cancellationToken);
                    sections.Add((FormatTitle(image, index), sectionText));

                    // 行框只对单张有意义：截图叠加层用它做「点行复制」
                    if (set.Images.Count == 1)
                        lines.AddRange(payload.Lines);
                }
                catch (OcrWorkerException ex)
                {
                    // 单张失败不能让整批停下
                    context.Host.Logger.LogWarning("OCR：{Name} 失败：{Error}", name, ex.Message);
                    sections.Add((name, string.Empty));
                }
            }
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

        var recognized = sections.Count(section => !string.IsNullOrWhiteSpace(section.Text));
        if (recognized == 0)
            return ToolResult.Failed("没有识别到文字", "图片无法解码、没有文字或 worker 异常");

        context.Progress?.Report(new ToolProgress(1, "整理结果"));

        // 单张不套标题：面板 / 截图结果应该是干净正文
        var text = set.Images.Count == 1
            ? sections[0].Text.Trim()
            : ComposeBatchText(sections);

        return ToolResult.Ok(new OcrResultPayload(text, lines));
    }

    /// <summary>
    /// 多张图拼成一份可读文本：识别成功的按文件分节，失败的集中在末尾说明。
    /// 纯函数，便于单测。
    /// </summary>
    public static string ComposeBatchText(IReadOnlyList<(string Name, string Text)> sections)
    {
        var builder = new StringBuilder();
        var failed = new List<string>();

        foreach (var (name, text) in sections)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                failed.Add(name);
                continue;
            }

            if (builder.Length > 0)
                builder.AppendLine();

            builder.Append("## ").AppendLine(name);
            builder.AppendLine();
            builder.AppendLine(text.TrimEnd());
        }

        if (failed.Count > 0)
        {
            if (builder.Length > 0)
                builder.AppendLine();

            builder.AppendLine("---");
            builder.Append("未识别：").Append(failed.Count).AppendLine(" 个文件");
            foreach (var name in failed)
                builder.Append("- ").AppendLine(name);
        }

        return builder.ToString().TrimEnd();
    }

    private static string DisplayName(ImageInput image, int index) =>
        string.IsNullOrWhiteSpace(image.Name) ? $"图片 {index + 1}" : image.Name;

    /// <summary>
    /// 多图时的分节标题：序号 + 名字 + 尺寸。
    /// 只写名字的话，几张剪贴板图片都叫「图片」，分不清哪段对应哪张。
    /// </summary>
    private static string FormatTitle(ImageInput image, int index)
    {
        var name = string.IsNullOrWhiteSpace(image.Name) ? "图片" : image.Name;
        var title = $"{index + 1}. {name}";

        if (image.Width > 0 && image.Height > 0)
            title += $"（{image.Width}×{image.Height}）";

        return title;
    }

    /// <summary>
    /// 可选翻译：把识别结果送给模型，按「原文 / 译文」对照拼回去。
    /// 没配模型或翻译失败都返回原文——识别是主任务，不能因为附加的翻译丢掉结果。
    /// </summary>
    private static async Task<string> MaybeTranslateAsync(
        ToolContext context,
        string original,
        string target,
        CancellationToken cancellationToken)
    {
        if (target == OcrParameters.TranslateNone || string.IsNullOrWhiteSpace(original))
            return original;

        if (context.Host is not { } host || !host.Ai.IsAvailable)
            return original;

        try
        {
            var response = await host.Ai.CompleteAsync(
                new AiRequest
                {
                    System =
                        $"把用户给出的文字翻译成{target}。只输出译文，不要解释、不要加引号，保留原有分段。",
                    Messages = [new AiMessage(AiRole.User, original)],
                    Temperature = 0.2,
                    MaxTokens = Math.Clamp(original.Length * 2, 512, 4096)
                },
                cancellationToken);

            var translated = response.Text.Trim();
            if (string.IsNullOrEmpty(translated))
                return original;

            return $"{original.TrimEnd()}\n\n—— 译文（{target}）——\n\n{translated}";
        }
        catch (AiException ex)
        {
            host.Logger.LogWarning("OCR 翻译失败：{Error}", ex.Message);
            return original;
        }
        catch (Exception ex)
        {
            host.Logger.LogWarning("OCR 翻译异常：{Error}", ex.Message);
            return original;
        }
    }
}
