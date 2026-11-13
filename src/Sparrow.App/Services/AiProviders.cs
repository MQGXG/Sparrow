namespace Sparrow.App.Services;

/// <summary>一个模型提供方的预设。全部走 OpenAI 兼容协议。</summary>
public sealed record AiProviderPreset(
    string Key,
    string Name,
    string BaseUrl,
    string Model,
    bool NeedsKey);

/// <summary>内置 provider 预设；「自定义」允许填任意兼容端点。</summary>
public static class AiProviders
{
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", true),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", true),
        new("qwen", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", true),
        new("zhipu", "智谱", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", true),
        new("moonshot", "Moonshot", "https://api.moonshot.cn/v1", "moonshot-v1-8k", true),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4o-mini", true),
        new("ollama", "Ollama（本地）", "http://localhost:11434/v1", "qwen2.5vl:7b", false),
        new("lmstudio", "LM Studio（本地）", "http://localhost:1234/v1", "local-model", false),
        new("custom", "自定义", "", "", true)
    ];

    public static AiProviderPreset? Find(string? key) =>
        All.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.OrdinalIgnoreCase));
}
