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
            OcrParameters.Translate,
            OcrParameters.Output
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

        var language = context.GetParameter(OcrParameters.LanguageKey, "ch");
        var layout = OcrParameters.ResolveLayout(context);
        var translateTo = context.GetParameter(OcrParameters.TranslateKey, OcrParameters.TranslateNone);
        var output = context.GetParameter(OcrParameters.OutputKey, OcrParameters.OutputText);

        // 表格不走本地 OCR：交给支持图片的视觉模型出 HTML / Markdown
        if (output is OcrParameters.OutputTableHtml or OcrParameters.OutputTableMarkdown)
        {
            return await RecognizeTableAsync(
                context.Host,
                set,
                markdown: output == OcrParameters.OutputTableMarkdown,
                cancellationToken);
        }

        // 进程插件唯一需要宿主提供的东西：自己在哪
        var workerPath = OcrParameters.WorkerPath(context.Host);

        if (!File.Exists(workerPath))
            return ToolResult.Failed($"OCR worker 不存在：{workerPath}");

        var sections = new List<TextSection>();
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
                    sections.Add(new TextSection
                    {
                        Title = FormatTitle(image, index),
                        SourceIndex = index,
                        Status = TextSectionStatus.Failed
                    });
                    continue;
                }

                try
                {
                    var workerOutput = await session.RecognizeAsync(image.Data, cancellationToken);

                    var error = OcrOutputComposer.TryReadError(workerOutput);
                    if (error is not null)
                    {
                        context.Host.Logger.LogWarning("OCR：{Name} 失败：{Error}", name, error);
                        sections.Add(new TextSection
                        {
                            Title = FormatTitle(image, index),
                            SourceIndex = index,
                            Status = TextSectionStatus.Failed
                        });
                        continue;
                    }

                    var payload = OcrOutputComposer.Compose(workerOutput, layout);

                    // 多图时标题带序号和尺寸，「这段字是哪张图」一眼能对上；
                    // 选了翻译就把原文和译文对照着放进同一段
                    var sectionText = await MaybeTranslateAsync(context, payload.Text, translateTo, cancellationToken);
                    sections.Add(new TextSection
                    {
                        Title = FormatTitle(image, index),
                        SourceIndex = index,
                        Text = sectionText,
                        Status = TextSectionStatus.Ok,
                        Lines = payload.Lines
                    });

                    // 行框只对单张有意义：截图叠加层用它做「点行复制」
                    if (set.Images.Count == 1)
                        lines.AddRange(payload.Lines);
                }
                catch (OcrWorkerException ex)
                {
                    // 单张失败不能让整批停下
                    context.Host.Logger.LogWarning("OCR：{Name} 失败：{Error}", name, ex.Message);
                    sections.Add(new TextSection
                    {
                        Title = FormatTitle(image, index),
                        SourceIndex = index,
                        Status = TextSectionStatus.Failed
                    });
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

        var recognized = sections.Count(section =>
            section.Status == TextSectionStatus.Ok && !string.IsNullOrWhiteSpace(section.Text));
        if (recognized == 0)
            return ToolResult.Failed("没有识别到文字", "图片无法解码、没有文字或 worker 异常");

        context.Progress?.Report(new ToolProgress(1, "整理结果"));

        // 单张不套标题：面板 / 截图结果应该是干净正文
        var text = set.Images.Count == 1
            ? sections[0].Text.Trim()
            : ComposeBatchText(sections);

        return ToolResult.Ok(new OcrResultPayload(text, lines, sections));
    }

    /// <summary>
    /// 多张图拼成一份可读文本：识别成功的按文件分节，失败的集中在末尾说明。
    /// 纯函数，便于单测。
    /// </summary>
    public static string ComposeBatchText(IReadOnlyList<TextSection> sections)
    {
        var builder = new StringBuilder();
        var failed = new List<string>();

        foreach (var section in sections)
        {
            if (section.Status != TextSectionStatus.Ok || string.IsNullOrWhiteSpace(section.Text))
            {
                failed.Add(section.Title);
                continue;
            }

            if (builder.Length > 0)
                builder.AppendLine();

            builder.Append("## ").AppendLine(section.Title);
            builder.AppendLine();
            builder.AppendLine(section.Text.TrimEnd());
        }

        if (failed.Count > 0)
        {
            if (builder.Length > 0)
                builder.AppendLine();

            builder.AppendLine("---");
            builder.Append("未识别：").Append(failed.Count).AppendLine(" 个文件");
            foreach (var title in failed)
                builder.Append("- ").AppendLine(title);
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

        if (context.Host is not { } host)
            return original;

        if (host.Ai.IsAvailable)
        {
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
                if (!string.IsNullOrEmpty(translated))
                    return $"{original.TrimEnd()}\n\n—— 译文（{target}）——\n\n{translated}";
            }
            catch (AiException ex)
            {
                host.Logger.LogWarning("OCR 翻译失败：{Error}", ex.Message);
            }
            catch (Exception ex)
            {
                host.Logger.LogWarning("OCR 翻译异常：{Error}", ex.Message);
            }
        }

        // 没配在线模型时用宿主下载好的本地模型（和「翻译」工具共用同一套引擎）
        var modelId = PickInstalledModel(host);
        if (modelId is not null && host.LocalTranslation.IsAvailable(modelId))
        {
            try
            {
                var translated = await host.LocalTranslation.TranslateAsync(
                    new LocalTranslationRequest
                    {
                        ModelId = modelId,
                        Text = original,
                        TargetLanguage = target
                    },
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(translated))
                    return $"{original.TrimEnd()}\n\n—— 译文（{target}）——\n\n{translated}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                host.Logger.LogWarning("OCR 本地翻译失败：{Error}", ex.Message);
            }
        }

        // 用户明确要求了翻译却没有可用的引擎：不能静默返回原文，
        // 否则会以为识别结果本来就是目标语言。
        return original.TrimEnd()
            + "\n\n—— 未翻译：没有可用的翻译引擎。"
            + "可在「设置 › 模型」启用在线模型，或下载一个离线模型——";
    }

    /// <summary>
    /// 表格识别：把图片交给视觉模型，直接产出 HTML / Markdown 表格。
    /// 本地 OCR 只做文字检测，不理解表格结构，所以这条路必须配视觉模型。
    /// </summary>
    private static async Task<ToolResult> RecognizeTableAsync(
        IToolHost host,
        ImageSetInput set,
        bool markdown,
        CancellationToken cancellationToken)
    {
        if (!host.Ai.IsAvailable)
        {
            return ToolResult.Unsupported(
                "表格识别需要支持图片的视觉模型，请先在「设置 › 模型」配置（如 qwen2.5vl）");
        }

        var format = markdown
            ? "Markdown 表格（第一行表头，第二行是 |---| 分隔行）"
            : "HTML 表格（<table>…</table>）";

        var system =
            $"把图片里的表格转成{format}，只输出表格本身，不要解释，不要加代码块围栏。"
            + "保留所有单元格文字，数字、货币符号、编号保持原样。"
            + (markdown
                ? "合并单元格无法表达时，把内容放在左上角单元格，其余留空。"
                : "合并单元格用 rowspan / colspan 表达。");

        var parts = new List<string>(set.Images.Count);

        foreach (var image in set.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var response = await host.Ai.CompleteAsync(
                    new AiRequest
                    {
                        System = system,
                        Messages = [new AiMessage(AiRole.User, "请转换这张图片里的表格。")],
                        Image = image.Data,
                        ImageMimeType = image.Format switch
                        {
                            ImageFormat.Jpeg => "image/jpeg",
                            ImageFormat.Webp => "image/webp",
                            ImageFormat.Bmp => "image/bmp",
                            _ => "image/png"
                        },
                        Temperature = 0,
                        MaxTokens = 4096
                    },
                    cancellationToken);

                var text = StripCodeFence(response.Text);
                if (text.Length == 0)
                    return ToolResult.Failed("表格识别没有返回内容，请确认图片里有表格");

                parts.Add(text);
            }
            catch (AiException ex)
            {
                return ex.Kind == AiErrorKind.NotConfigured
                    ? ToolResult.Unsupported(ex.Message)
                    : ToolResult.Failed(ex.Message);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Cancelled();
            }
        }

        return ToolResult.Ok(new OcrResultPayload(string.Join("\n\n", parts), []));
    }

    /// <summary>模型偶尔会加 ```html / ``` 围栏，去掉再返回。</summary>
    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLine < 0 || lastFence <= firstLine)
            return trimmed;

        return trimmed[(firstLine + 1)..lastFence].Trim();
    }

    /// <summary>挑宿主编译清单里第一个已下载的本地模型；没有就返回 null。</summary>
    private static string? PickInstalledModel(IToolHost host)
    {
        var models = host.LocalModels;

        foreach (var model in models.All)
        {
            if (models.IsInstalled(model.Id))
                return model.Id;
        }

        return null;
    }
}
