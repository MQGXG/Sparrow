namespace Sparrow.Plugin.Sdk;

/// <summary>AI 对话消息角色。</summary>
public enum AiRole
{
    System = 0,
    User,
    Assistant
}

/// <summary>一条对话消息。</summary>
public sealed record AiMessage(AiRole Role, string Text);

/// <summary>
/// 一次模型调用请求。
/// 插件只描述「要什么」，provider、端点、密钥由宿主决定。
/// </summary>
public sealed record AiRequest
{
    /// <summary>系统提示词；为空时由 provider 使用默认值。</summary>
    public string? System { get; init; }

    public IReadOnlyList<AiMessage> Messages { get; init; } = Array.Empty<AiMessage>();

    /// <summary>可选图片（视觉模型用）；为空表示纯文本。</summary>
    public ReadOnlyMemory<byte> Image { get; init; }

    /// <summary>图片 MIME 类型，如 image/png。</summary>
    public string? ImageMimeType { get; init; }

    public double Temperature { get; init; } = 0.2;

    public int MaxTokens { get; init; } = 1024;
}

/// <summary>模型返回的文本与用量。</summary>
public sealed record AiResponse(string Text)
{
    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }
}

/// <summary>模型调用失败的类型；插件据此决定是提示配置还是提示重试。</summary>
public enum AiErrorKind
{
    NotConfigured = 0,
    Unauthorized,
    RateLimited,
    Timeout,
    ProviderError,
    InvalidResponse
}

/// <summary>宿主抛给插件的模型调用异常。</summary>
public sealed class AiException : Exception
{
    public AiException(AiErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public AiErrorKind Kind { get; }
}

/// <summary>
/// 宿主提供的模型能力。
/// 插件不得自己保存 API Key、拼 Base URL 或 new HttpClient。
/// </summary>
public interface IAiClient
{
    /// <summary>用户是否已启用并配置好模型。为 false 时插件应返回 Unsupported。</summary>
    bool IsAvailable { get; }

    /// <summary>当前模型名，仅用于展示与日志。</summary>
    string? ModelName { get; }

    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}
