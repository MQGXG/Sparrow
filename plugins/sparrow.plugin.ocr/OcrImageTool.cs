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
    private readonly WarmWorkerPool _pool;

    public OcrImageTool() : this(new ProcessOcrWorkerRunner())
    {
    }

    public OcrImageTool(IOcrWorkerRunner runner) : this(runner, WarmWorkerPool.DefaultIdleTimeout)
    {
    }

    internal OcrImageTool(IOcrWorkerRunner runner, TimeSpan? idleTimeout) =>
        _pool = new WarmWorkerPool(runner, idleTimeout);

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
            OcrParameters.Output,
            OcrParameters.Reuse
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
        var keepWarm =
            context.GetParameter(OcrParameters.ReuseKey, OcrParameters.ReuseAuto)
            != OcrParameters.ReuseOff;

        // 表格：在线视觉模型出 HTML；失败或未配置时回落本地 SLANet
        if (output is OcrParameters.OutputTableHtml or OcrParameters.OutputTableMarkdown)
        {
            return await RecognizeTableAsync(
                context.Host,
                set,
                language,
                markdown: output == OcrParameters.OutputTableMarkdown,
                keepWarm,
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
            await using var session = await _pool.AcquireAsync(
                workerPath, language, "text", keepWarm, cancellationToken);

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

                    // 翻译：整段拼给文本区，同时按行回填给「译文叠层」用
                    var translated = await TranslateTextAsync(context, payload.Text, translateTo, cancellationToken);
                    var sectionText = translated is null
                        ? payload.Text
                        : $"{payload.Text.TrimEnd()}\n\n—— 译文（{translateTo}）——\n\n{translated}";

                    var sectionLines = translated is null
                        ? payload.Lines
                        : AttachTranslations(payload.Lines, translated);

                    sections.Add(new TextSection
                    {
                        Title = FormatTitle(image, index),
                        SourceIndex = index,
                        Text = sectionText,
                        Status = TextSectionStatus.Ok,
                        Lines = sectionLines
                    });

                    // 行框只对单张有意义：截图叠加层用它做「点行复制」和译文叠层
                    if (set.Images.Count == 1)
                        lines.AddRange(sectionLines);
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
    /// 把译文按行拆开回填到行框。行数对不上就不回填——
    /// 宁可不叠译文，也不把句子叠到错的格子里。
    /// </summary>
    internal static IReadOnlyList<OcrTextLine> AttachTranslations(
        IReadOnlyList<OcrTextLine> lines, string translated)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(translated))
            return lines;

        var parts = translated
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

        if (parts.Count != lines.Count)
            return lines;

        var result = new List<OcrTextLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
            result.Add(lines[index] with { Translation = parts[index] });

        return result;
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
        var translated = await TranslateTextAsync(context, original, target, cancellationToken);
        return translated is null
            ? original
            : $"{original.TrimEnd()}\n\n—— 译文（{target}）——\n\n{translated}";
    }

    /// <summary>
    /// 只返回译文（不含原文和标题）。识别是主任务，翻译失败返回 null，调用方保留原文。
    /// 单独抽出来是为了给行框回填 `Translation`（译文叠层要用）。
    /// </summary>
    private static async Task<string?> TranslateTextAsync(
        ToolContext context,
        string original,
        string target,
        CancellationToken cancellationToken)
    {
        if (target == OcrParameters.TranslateNone || string.IsNullOrWhiteSpace(original))
            return null;

        if (context.Host is not { } host)
            return null;

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
                    return translated;
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
                    return translated;
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
    /// 表格识别：在线视觉模型优先（HTML），失败或未配置时回落本地 SLANet。
    /// 两条路都只产出 HTML，Markdown 在本地转换。
    /// </summary>
    private async Task<ToolResult> RecognizeTableAsync(
        IToolHost host,
        ImageSetInput set,
        string language,
        bool markdown,
        bool keepWarm,
        CancellationToken cancellationToken)
    {
        // 在线优先；在线失败（超时、网络、额度）再回落本地表格模型
        if (host.Ai.IsAvailable)
        {
            var online = await RecognizeTableWithAiAsync(host, set, markdown, cancellationToken);
            if (online.Success)
                return online;

            if (online.Error?.Kind == ToolErrorKind.Cancelled)
                return online;

            host.Logger.LogWarning("表格识别：在线模型失败，改用本地表格模型（{Error}）", online.Error?.Message);
        }

        return await RecognizeTableLocallyAsync(host, set, language, markdown, keepWarm, cancellationToken);
    }

    private static async Task<ToolResult> RecognizeTableWithAiAsync(
        IToolHost host,
        ImageSetInput set,
        bool markdown,
        CancellationToken cancellationToken)
    {
        // 统一要 HTML：实测模型不遵守「输出 Markdown」，干脆本地转，转换结果也确定。
        const string system =
            "把图片里的表格转成 HTML 表格（<table>…</table>），只输出表格本身，不要解释，不要加代码块围栏。"
            + "保留所有单元格文字，数字、货币符号、编号保持原样。合并单元格用 rowspan / colspan 表达。";

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

                var html = StripCodeFence(response.Text);
                if (html.Length == 0)
                    return ToolResult.Failed("表格识别没有返回内容，请确认图片里有表格");

                // Markdown 由本地转换，模型只负责出 HTML
                parts.Add(markdown ? HtmlTable.ToMarkdown(html) ?? html : html);
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

    /// <summary>本地表格识别：SLANet 模型随 worker 分发，离线可用。</summary>
    private async Task<ToolResult> RecognizeTableLocallyAsync(
        IToolHost host,
        ImageSetInput set,
        string language,
        bool markdown,
        bool keepWarm,
        CancellationToken cancellationToken)
    {
        var workerPath = OcrParameters.WorkerPath(host);

        if (!File.Exists(workerPath))
            return ToolResult.Failed($"OCR worker 不存在：{workerPath}");

        try
        {
            // 结构模型不分语言；语言只影响回填单元格文字用的文字模型
            await using var session = await _pool.AcquireAsync(
                workerPath, language, "table", keepWarm, cancellationToken);

            var parts = new List<string>(set.Images.Count);

            foreach (var image in set.Images)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var raw = await session.RecognizeAsync(image.Data, cancellationToken);

                var error = OcrOutputComposer.TryReadError(raw);
                if (error is not null)
                    return ToolResult.Failed($"表格识别失败：{error}");

                var html = OcrOutputComposer.TryReadTable(raw);
                if (html is null)
                    return ToolResult.Failed("表格识别返回了无法解析的结果");

                parts.Add(markdown ? HtmlTable.ToMarkdown(html) ?? html : html);
            }

            return ToolResult.Ok(new OcrResultPayload(string.Join("\n\n", parts), []));
        }
        catch (OcrWorkerException ex)
        {
            return ToolResult.Failed($"表格识别失败：{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Cancelled();
        }
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
