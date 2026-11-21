using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging.Abstractions;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 本地翻译引擎（GGUF / llama.cpp）。
///
/// 这是「翻译能力」而不是「翻译工具」：翻译插件和 OCR 的「翻译为」都通过
/// <see cref="ILocalTranslation"/> 调它，提示词、采样、分块、清理只有这一份实现。
///
/// 模型只在一次翻译期间加载，调用结束立刻释放：1.8B 常驻要 1GB 以上，
/// 7B 要 4GB 以上，闲置时不占着更符合这个工具的定位。
/// 也不做静态缓存 + 定时释放——定时器会把程序集钉在进程里。
/// </summary>
internal sealed class LocalTranslationService : ILocalTranslation
{
    private const int ContextSize = 4096;

    /// <summary>离线模型上下文小，块切得短，避免 4096 被吃掉。</summary>
    private const int ChunkLength = 1200;

    /// <summary>最多 20 块（约 2.4 万字）；再多就是让用户等十几分钟，不如直接拒绝。</summary>
    private const int MaxChunks = 20;

    /// <summary>同一时间只跑一个推理：模型加载和上下文都不是线程安全的。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>常见 ChatML / Gemma 的结束标记，模型自带 EOS 之外再兜一层。</summary>
    private static readonly string[] StopSequences =
        ["<|eot_id|>", "<|im_end|>", "<|endoftext|>", "<|end_of_text|>", "<end_of_turn>"];

    // 混元翻译的特殊 token。竖线是 U+FF5C、下划空位是 U+2581，不是普通 ASCII 字符。
    private const string HunyuanBegin = "<\uFF5Chy_begin\u2581of\u2581sentence\uFF5C>";
    private const string HunyuanUser = "<\uFF5Chy_User\uFF5C>";
    private const string HunyuanAssistant = "<\uFF5Chy_Assistant\uFF5C>";

    private static bool _nativeConfigured;

    private readonly ILocalModels _models;
    private readonly IAppLog _log;

    public LocalTranslationService(ILocalModels models, IAppLog log)
    {
        _models = models;
        _log = log;
    }

    public bool IsAvailable(string modelId) => _models.IsInstalled(modelId);

    public async Task<string?> TranslateAsync(
        LocalTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return null;

        if (!_models.IsInstalled(request.ModelId))
        {
            _log.Warn($"本地翻译：模型「{request.ModelId}」未下载");
            return null;
        }

        var chunks = Chunk(request.Text, ChunkLength).ToList();
        if (chunks.Count > MaxChunks)
        {
            _log.Warn($"本地翻译：文本 {request.Text.Length} 字，超过 {MaxChunks} 块的限制");
            return null;
        }

        var path = await _models.EnsureAsync(request.ModelId, progress: null, cancellationToken);
        if (path is null)
        {
            _log.Warn($"本地翻译：模型「{request.ModelId}」不可用");
            return null;
        }

        await Gate.WaitAsync(cancellationToken);

        try
        {
            ConfigureNative();

            var parameters = CreateParameters(path);

            // 加载 1.1GB 模型要几秒，放线程池：调用方通常在 UI 线程上 await
            var weights = await Task.Run(
                () => LLamaWeights.LoadFromFile(parameters),
                cancellationToken);

            using (weights)
            {
                // 提示词由本类用模型自带的 chat template 渲染，执行器不要再套一次。
                // 注意：加载时用的 ModelParams 会被 LoadFromFile 改掉，执行器必须另建一份，
                // 否则 7B 会把指令当成待译文本复述出来（1.8B 碰巧不受影响）。
                var executor = new StatelessExecutor(weights, CreateParameters(path), NullLogger.Instance)
                {
                    ApplyTemplate = false
                };

                var glossary = BuildGlossary(request.Terms);
                var results = new List<string>(chunks.Count);

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var prompt = RenderPrompt(weights, request, glossary, chunk);
                    var options = new InferenceParams
                    {
                        MaxTokens = Math.Clamp(chunk.Length * 2, 256, 3072),
                        AntiPrompts = StopSequences,
                        SamplingPipeline = CreateSampling()
                    };

                    var builder = new StringBuilder();

                    await foreach (var token in executor.InferAsync(prompt, options, cancellationToken))
                        builder.Append(token);

                    results.Add(Clean(builder.ToString()));
                }

                return string.Concat(results).Trim();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"本地翻译失败：{ex.Message}", ex);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// native 库跟着宿主走（App 目录下的 runtimes/win-x64/native）。
    /// 不显式指过去会报「native library cannot be correctly loaded」。
    /// </summary>
    private static void ConfigureNative()
    {
        if (_nativeConfigured)
            return;

        NativeLibraryConfig.All
            .WithSearchDirectory(AppContext.BaseDirectory)
            .WithAutoFallback(true);

        _nativeConfigured = true;
    }

    private static ModelParams CreateParameters(string modelPath) => new(modelPath)
    {
        ContextSize = ContextSize,
        GpuLayerCount = 0,
        Threads = Math.Max(2, Environment.ProcessorCount - 1)
    };

    /// <summary>混元翻译模型卡推荐的采样参数；温度 0 会让专有名词翻得更死板。</summary>
    private static DefaultSamplingPipeline CreateSampling() => new()
    {
        Temperature = 0.7f,
        TopK = 20,
        TopP = 0.6f,
        RepeatPenalty = 1.05f
    };

    /// <summary>
    /// 渲染提示词。
    ///
    /// 混元翻译按模型卡手工拼：llama.cpp 的 Jinja 引擎解析这个 GGUF 的 chat_template 有误
    /// （会把 &lt;hy_User&gt; 挪到正文后面，译文就退化成原文）。
    /// 其余模型仍交给自带的 chat template。
    /// </summary>
    private static string RenderPrompt(
        LLamaWeights weights,
        LocalTranslationRequest request,
        string glossary,
        string text)
    {
        var user = BuildInstruction(request.TargetLanguage, request.Style, request.KeepFormatting) + text;

        // 混元 1.8B 的模板用全角竖线（<｜hy_User｜>），llama.cpp 解析会错位：
        // 手工拼，并把术语表放到 <hy_User> 之前（system 位）。
        if (IsLegacyHunyuanTemplate(weights))
            return HunyuanBegin + glossary + HunyuanUser + user + HunyuanAssistant;

        // 7B 用 <|startoftext|>…<|extra_0|>：指令走 system 位（<|extra_4|>），正文走 user。
        // 实测指令和正文混在一条 user 消息里时，7B 会把指令当成待译文本复述一遍。
        try
        {
            var template = new LLamaTemplate(weights) { AddAssistant = true };
            template.Add(
                "system",
                BuildInstruction(request.TargetLanguage, request.Style, request.KeepFormatting) + glossary);
            template.Add("user", text);

            var rendered = Encoding.UTF8.GetString(template.Apply());
            if (!string.IsNullOrWhiteSpace(rendered))
                return rendered;
        }
        catch (Exception)
        {
            // 模板缺失或渲染失败都退回原始提示，翻译还能继续
        }

        return user;
    }

    /// <summary>
    /// 1.8B 的模板里是 <c>&lt;｜hy_User｜&gt;</c>（全角竖线），需要手工拼；
    /// 7B 换成了 <c>&lt;|startoftext|&gt;</c> / <c>&lt;|extra_0|&gt;</c>，模板引擎能正确解析。
    /// </summary>
    private static bool IsLegacyHunyuanTemplate(LLamaWeights weights) =>
        weights.Metadata.TryGetValue("tokenizer.chat_template", out var template)
        && template.Contains("hy_User", StringComparison.Ordinal);

    /// <summary>
    /// 混元翻译官方的两种提示：目标含中文用中文指令，其余用英文指令。
    /// 语气和格式只在用户改了默认值时才追加，保持官方模板的原始形态。
    /// </summary>
    private static string BuildInstruction(string target, string style, bool keepFormatting)
    {
        var builder = new StringBuilder();

        if (target == "中文")
        {
            // 只保留「专有名词保持原文」这一句：
            // 再加「只使用中文」会被 1.8B 当成正文，翻译完在结尾复述一遍（实测 3/3 复现）。
            // 漏出来的英文连接词交给 TranslationCleanup 在输出侧清理。
            builder.Append("将以下文本翻译为中文，注意只需要输出翻译后的结果，不要额外解释。")
                .Append("产品名、专有名词、代码和链接保持原文：\n\n");
        }
        else
        {
            builder.Append("Translate the following segment into ")
                .Append(EnglishName(target))
                .Append(", without additional explanation.")
                .Append(" Keep product names, proper nouns, code and links unchanged:\n\n");
        }

        if (style != "自然")
            builder.Append("语气：").Append(style).Append("。\n");

        if (!keepFormatting)
            builder.Append("只输出连续段落，不要保留复杂排版。\n");

        // 提示和正文之间留一行，和模型卡里的示例一致
        return builder.Append('\n').ToString();
    }

    /// <summary>常见目标语言给英文名（混元模型卡用的是英文）；其余原样输出。</summary>
    internal static string EnglishName(string target) => target switch
    {
        "English" => "English",
        "日本語" => "Japanese",
        "한국어" => "Korean",
        "Русский" => "Russian",
        "Français" => "French",
        "Deutsch" => "German",
        "Español" => "Spanish",
        "Português" => "Portuguese",
        "العربية" => "Arabic",
        "ไทย" => "Thai",
        "Tiếng Việt" => "Vietnamese",
        _ => target
    };

    private static string BuildGlossary(IReadOnlyList<LocalTranslationTerm> terms)
    {
        if (terms.Count == 0)
            return string.Empty;

        var builder = new StringBuilder("术语表（这些词必须按指定译法翻译）：\n");

        foreach (var term in terms)
            builder.Append(term.Source).Append(" → ").Append(term.Target).Append('\n');

        return builder.Append('\n').ToString();
    }

    /// <summary>按长度切块，优先在换行/句末标点处断开，避免把句子劈开。</summary>
    private static IEnumerable<string> Chunk(string text, int maxLength)
    {
        var start = 0;

        while (start < text.Length)
        {
            var length = Math.Min(maxLength, text.Length - start);

            if (start + length < text.Length)
            {
                var cut = text.LastIndexOfAny(
                    ['\n', '。', '！', '？', '.', '!', '?', '；', ';'],
                    start + length - 1,
                    length);

                if (cut > start + maxLength / 3)
                    length = cut - start + 1;
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }

    /// <summary>小模型偶尔会自作主张加一对引号，去掉；再清理漏出的英文连接词。</summary>
    private static string Clean(string text)
    {
        var trimmed = TranslationCleanup.Apply(text).Trim();

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '“' && trimmed[^1] == '”')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }
}
