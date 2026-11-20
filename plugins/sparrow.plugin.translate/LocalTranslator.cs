using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sparrow.Plugin.Translate;

/// <summary>
/// 离线翻译推理（llama.cpp / LLamaSharp，GGUF 模型）。
///
/// 模型只在一次翻译期间加载，调用结束立刻释放：
/// 1.8B 模型常驻要 1GB 以上内存，7B 要 4GB 以上——闲置时不占着更符合这个工具的定位。
/// 也不做静态缓存 + 定时释放：定时器会把插件程序集钉在进程里，宿主卸载插件时
/// ALC 回收不掉（见 docs/architecture.md 铁律 4）。
/// </summary>
internal static class LocalTranslator
{
    private const int ContextSize = 4096;

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

    /// <summary>
    /// 逐块翻译。返回顺序与 <paramref name="chunks"/> 一致。
    /// 抛出的异常由调用方转成可读提示（这里只做「把资源放掉」）。
    /// </summary>
    public static async Task<IReadOnlyList<string>> TranslateAsync(
        string modelPath,
        string pluginDirectory,
        IReadOnlyList<string> chunks,
        string targetLanguage,
        string style,
        bool keepFormatting,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            ConfigureNative(pluginDirectory);

            var parameters = CreateParameters(modelPath);

            // 加载 1.1GB 模型要几秒，放线程池：调用方在 UI 线程上 await，
            // 直接同步加载会把面板冻住。
            var weights = await Task.Run(
                () => LLamaWeights.LoadFromFile(parameters),
                cancellationToken);

            using (weights)
            {
                // 提示词由本类用模型自带的 chat template 渲染，执行器不要再套一次
                var executor = new StatelessExecutor(weights, parameters, NullLogger.Instance)
                {
                    ApplyTemplate = false
                };

                var results = new List<string>(chunks.Count);

                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 不拼「上文」：混元是专用翻译模型，把参考上下文塞进正文会被当成待译文本，
                    // 实测会在开头复述上一段。相邻块之间它自己的术语已经够稳定。
                    var prompt = RenderPrompt(weights, targetLanguage, style, keepFormatting, chunk);

                    var options = new InferenceParams
                    {
                        MaxTokens = Math.Clamp(chunk.Length * 2, 256, 3072),
                        AntiPrompts = StopSequences,
                        SamplingPipeline = CreateSampling()
                    };

                    var builder = new StringBuilder();

                    await foreach (var token in executor.InferAsync(prompt, options, cancellationToken))
                        builder.Append(token);

                    results.Add(builder.ToString());
                }

                return results;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 插件目录里有 runtimes/win-x64/native，默认搜索路径只认宿主目录，
    /// 不显式指过去会报「native library cannot be correctly loaded」。
    /// </summary>
    private static void ConfigureNative(string pluginDirectory)
    {
        if (_nativeConfigured)
            return;

        NativeLibraryConfig.All
            .WithSearchDirectory(pluginDirectory)
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
    /// （会把 &lt;hy_User&gt; 挪到正文后面，译文就退化成了原文）。
    /// 其余模型仍交给自带的 chat template。
    /// </summary>
    private static string RenderPrompt(
        LLamaWeights weights,
        string target,
        string style,
        bool keepFormatting,
        string text)
    {
        var user = BuildInstruction(target, style, keepFormatting) + text;

        if (IsHunyuan(weights))
            return HunyuanBegin + HunyuanUser + user + HunyuanAssistant;

        try
        {
            var template = new LLamaTemplate(weights) { AddAssistant = true };
            template.Add("user", user);

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

    /// <summary>架构取自 GGUF 元数据，换模型时不会误用混元的模板。</summary>
    private static bool IsHunyuan(LLamaWeights weights) =>
        weights.Metadata.TryGetValue("general.architecture", out var architecture)
        && architecture.StartsWith("hunyuan", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 混元翻译官方的两种提示：目标含中文用中文指令，其余用英文指令。
    /// 语气和格式只在用户改了默认值时才追加，保持官方模板的原始形态。
    /// </summary>
    private static string BuildInstruction(string target, string style, bool keepFormatting)
    {
        var builder = new StringBuilder();

        if (target == "中文")
        {
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
    private static string EnglishName(string target) => target switch
    {
        "English" => "English",
        "日本語" => "Japanese",
        "한국어" => "Korean",
        "Русский" => "Russian",
        "Français" => "French",
        "Deutsch" => "German",
        "Español" => "Spanish",
        _ => target
    };
}
