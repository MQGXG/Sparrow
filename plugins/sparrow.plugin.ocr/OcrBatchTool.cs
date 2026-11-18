using System.Text;
using Microsoft.Extensions.Logging;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Ocr;

/// <summary>
/// 批量图片识别：一次处理多个图片文件，按文件拼成一份文本。
///
/// 和单张工具共用一个 worker 会话——模型只加载一次，否则「批量」会被
/// 每张图 1-3 秒的初始化淹没。文件读取走宿主的 <see cref="IFileAccess"/>，
/// 插件依然不碰文件系统，只能读本次输入里出现的路径。
/// </summary>
public sealed class OcrBatchTool : ITool
{
    private const string ParameterWithFileName = "withFileName";

    private readonly IOcrWorkerRunner _runner;

    public OcrBatchTool() : this(new ProcessOcrWorkerRunner())
    {
    }

    public OcrBatchTool(IOcrWorkerRunner runner) => _runner = runner;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.ocr.batch",
        Name = "批量图片识别",
        Category = "ocr",
        Description = "一次识别多个图片文件，结果按文件分节",
        Keywords = ["ocr", "批量", "识别", "多图", "图片", "文字"],
        Accepts = InputShape.Files,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.Ocr,
        Order = 31,
        Parameters =
        [
            OcrParameters.Language,
            OcrParameters.Layout,
            new ToolParameter
            {
                Key = ParameterWithFileName,
                Label = "文件名标题",
                Kind = ParameterKind.Toggle,
                Default = "true",
                Hint = "每段结果前加上文件名，方便对照原图"
            }
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not FileSetInput files)
            return ToolResult.InvalidInput("需要文件输入");

        if (files.Paths.Count == 0)
            return ToolResult.InvalidInput("没有可识别的文件");

        if (context.Host is null)
            return ToolResult.Failed("宿主未注入能力，无法定位 OCR worker");

        if (context.Files is null)
            return ToolResult.Failed("宿主未提供文件读取能力");

        var workerPath = OcrParameters.WorkerPath(context.Host);
        if (!File.Exists(workerPath))
            return ToolResult.Failed($"OCR worker 不存在：{workerPath}");

        var language = context.GetParameter(OcrParameters.LanguageKey, "ch");
        var layout = OcrParameters.ResolveLayout(context);
        var withFileName = context.GetParameter(ParameterWithFileName, true);

        var sections = new List<(string Name, string Text)>();
        var lines = new List<OcrTextLine>();

        try
        {
            await using var session = await _runner.StartAsync(workerPath, language, cancellationToken);

            for (var index = 0; index < files.Paths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = files.Paths[index];
                var name = Path.GetFileName(path);

                context.Progress?.Report(new ToolProgress(
                    index / (double)files.Paths.Count,
                    $"正在识别 {name}"));

                if (!OcrParameters.IsImage(path))
                {
                    sections.Add((name, string.Empty));
                    continue;
                }

                var bytes = context.Files.ReadAllBytes(path);
                if (bytes is null || bytes.Length == 0)
                {
                    sections.Add((name, string.Empty));
                    continue;
                }

                try
                {
                    var output = await session.RecognizeAsync(bytes, cancellationToken);

                    var error = OcrOutputComposer.TryReadError(output);
                    if (error is not null)
                    {
                        context.Host.Logger.LogWarning("批量识别：{File} 失败：{Error}", name, error);
                        sections.Add((name, string.Empty));
                        continue;
                    }

                    var payload = OcrOutputComposer.Compose(output, layout);
                    sections.Add((name, payload.Text));
                    lines.AddRange(payload.Lines);
                }
                catch (OcrWorkerException ex)
                {
                    // 单张失败不能让整批停下：记日志，继续下一张
                    context.Host.Logger.LogWarning("批量识别：{File} 失败：{Error}", name, ex.Message);
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
            context.Host.Logger.LogError(ex, "批量识别执行失败");
            return ToolResult.Failed("批量识别失败", ex.Message);
        }

        var recognized = sections.Count(section => !string.IsNullOrWhiteSpace(section.Text));
        if (recognized == 0)
            return ToolResult.Failed("没有识别到文字", BuildFailureDetail(files.Paths));

        context.Progress?.Report(new ToolProgress(1, "整理结果"));

        var text = ComposeBatchText(sections, withFileName);
        return ToolResult.Ok(new OcrResultPayload(text, lines));
    }

    /// <summary>
    /// 拼成一份可读文本：识别成功的按文件分节，失败的集中在末尾说明。
    /// 纯函数，便于单测。
    /// </summary>
    public static string ComposeBatchText(
        IReadOnlyList<(string Name, string Text)> sections,
        bool withFileName)
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

            if (withFileName)
            {
                builder.Append("## ").AppendLine(name);
                builder.AppendLine();
            }

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

    private static string BuildFailureDetail(IReadOnlyList<string> paths)
    {
        var notImage = paths.Count(path => !OcrParameters.IsImage(path));
        return notImage == paths.Count
            ? "选中的文件都不是图片（支持 png / jpg / webp / bmp / gif / tiff）"
            : "图片无法读取或没有文字";
    }
}
