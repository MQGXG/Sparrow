using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// OpenAI 兼容的模型客户端。
///
/// 云端和本地用同一套协议：OpenAI / DeepSeek / Qwen / 智谱 / Ollama 都走
/// <c>{baseUrl}/chat/completions</c>。
/// Key 只从 <see cref="AiSecretStore"/> 读取，不写日志、不传给插件。
/// </summary>
public sealed class AiClient : IAiClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AiSecretStore _secrets;
    private readonly IAppLog _log;
    private readonly HttpClient _http;

    private AppSettings _settings = new();
    private string? _apiKey;

    public AiClient(AiSecretStore secrets, IAppLog log)
        : this(secrets, log, null)
    {
    }

    internal AiClient(AiSecretStore secrets, IAppLog log, HttpMessageHandler? handler)
    {
        _secrets = secrets;
        _log = log;
        _http = handler is null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>设置页保存后调用：重新读取开关、端点和密钥。</summary>
    public void Configure(AppSettings settings)
    {
        _settings = settings;
        _apiKey = _secrets.Load();
    }

    public bool IsAvailable =>
        _settings.AiEnabled
        && !string.IsNullOrWhiteSpace(_settings.AiBaseUrl)
        && !string.IsNullOrWhiteSpace(_settings.AiModel)
        && (!string.IsNullOrWhiteSpace(_apiKey) || IsLoopback(_settings.AiBaseUrl));

    public string? ModelName => _settings.AiModel;

    /// <summary>
    /// 发送前的确认钩子。返回 false 表示用户取消，请求不会发出去。
    ///
    /// 由宿主在启动时接上（弹窗展示 provider / 模型 / 将要发送的内容）；
    /// 插件只拿到 <c>IAiClient</c>，不知道有没有确认，也无法绕过。
    /// </summary>
    public Func<AiRequest, Task<bool>>? ConfirmBeforeSend { get; set; }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        EnsureUsable(request);

        if (ConfirmBeforeSend is { } confirm && !await confirm(request))
        {
            // 用「已取消」表达用户拒绝发送：各工具的 catch 已经把它转成 ToolResult.Cancelled
            throw new OperationCanceledException("用户取消了发送。");
        }

        var payload = BuildPayload(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var httpRequest = CreateRequest(payload);

        var startedAt = Stopwatch.GetTimestamp();
        var response = await SendAsync(httpRequest, cancellationToken, timeout.Token);

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
                throw MapHttpError(response.StatusCode, body);

            var result = ParseResponse(body);
            var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            // 只记录元数据，不记录 prompt / 回复正文
            _log.Info(
                $"AI 调用：{_settings.AiModel}，{elapsed:F0} ms，"
                + $"tokens {result.PromptTokens?.ToString() ?? "?"}/"
                + $"{result.CompletionTokens?.ToString() ?? "?"}");

            return result;
        }
    }

    /// <summary>
    /// 流式调用（OpenAI 兼容 SSE）。每次 yield 累积文本，方便调用方直接重画。
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        AiRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureUsable(request);

        if (ConfirmBeforeSend is { } confirm && !await confirm(request))
            throw new OperationCanceledException("用户取消了发送。");

        var payload = BuildPayload(request);
        payload["stream"] = true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var httpRequest = CreateRequest(payload);
        var response = await SendAsync(httpRequest, cancellationToken, timeout.Token);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                throw MapHttpError(response.StatusCode, body);
            }

            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var builder = new StringBuilder();
            var received = false;

            while (true)
            {
                // ReadLineAsync 带取消：用户关窗 / 超时都从这里退出
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null)
                    break;

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var data = line[5..].Trim();
                if (data.Length == 0)
                    continue;

                if (data == "[DONE]")
                    break;

                var delta = ParseDelta(data);
                if (string.IsNullOrEmpty(delta))
                    continue;

                builder.Append(delta);
                received = true;

                yield return builder.ToString();
            }

            if (!received)
                throw new AiException(AiErrorKind.InvalidResponse, "模型流式返回了空内容。");
        }
    }

    /// <summary>一次性调用与流式调用共用的前置校验。</summary>
    private void EnsureUsable(AiRequest request)
    {
        if (!IsAvailable)
        {
            throw new AiException(
                AiErrorKind.NotConfigured,
                "未配置 AI 模型。请先在设置 › 模型里选择提供方并填写密钥。");
        }

        if (request.Messages.Count == 0 && string.IsNullOrWhiteSpace(request.System))
            throw new ArgumentException("AiRequest 至少需要一条消息或系统提示词。", nameof(request));
    }

    private HttpRequestMessage CreateRequest(JsonObject payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_settings.AiBaseUrl));

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, SerializerOptions),
            Encoding.UTF8,
            "application/json");

        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken userToken,
        CancellationToken timeoutToken)
    {
        try
        {
            return await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutToken);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            throw new AiException(AiErrorKind.Timeout, "模型请求超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new AiException(AiErrorKind.ProviderError, $"无法连接模型服务：{ex.Message}", ex);
        }
    }

    /// <summary>从一条 SSE data 里取增量文本；不是合法帧返回 null。</summary>
    private static string? ParseDelta(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return null;
            }

            if (!choices[0].TryGetProperty("delta", out var delta)
                || !delta.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return content.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private JsonObject BuildPayload(AiRequest request)
    {
        var messages = new JsonArray();
        var imageUrl = BuildImageDataUrl(request);

        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.System
            });
        }

        var lastUserIndex = -1;
        for (var i = 0; i < request.Messages.Count; i++)
        {
            if (request.Messages[i].Role == AiRole.User)
                lastUserIndex = i;
        }

        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            var role = message.Role switch
            {
                AiRole.System => "system",
                AiRole.Assistant => "assistant",
                _ => "user"
            };

            if (imageUrl is not null && i == lastUserIndex)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = message.Text },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = imageUrl }
                        }
                    }
                });
            }
            else
            {
                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = message.Text
                });
            }
        }

        // 只有图片、没有文字消息时，补一条空的 user 消息承载图片
        if (imageUrl is not null && lastUserIndex < 0)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "" },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = imageUrl }
                    }
                }
            });
        }

        return new JsonObject
        {
            ["model"] = _settings.AiModel,
            ["messages"] = messages,
            ["temperature"] = Math.Clamp(request.Temperature, 0, 2),
            ["max_tokens"] = Math.Clamp(request.MaxTokens, 1, 32_000)
        };
    }

    private static string? BuildImageDataUrl(AiRequest request)
    {
        if (request.Image.IsEmpty)
            return null;

        var mime = string.IsNullOrWhiteSpace(request.ImageMimeType)
            ? "image/png"
            : request.ImageMimeType;

        return $"data:{mime};base64,{Convert.ToBase64String(request.Image.Span)}";
    }

    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";
    }

    private static bool IsLoopback(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static AiException MapHttpError(HttpStatusCode status, string body)
    {
        var detail = body.Length <= 200 ? body : body[..200] + "…";

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AiException(AiErrorKind.Unauthorized, "API Key 无效或没有权限。"),

            HttpStatusCode.TooManyRequests =>
                new AiException(AiErrorKind.RateLimited, "请求过于频繁，请稍后重试。"),

            HttpStatusCode.NotFound =>
                new AiException(AiErrorKind.ProviderError, "模型端点不存在，请检查 Base URL 和模型名。"),

            >= HttpStatusCode.InternalServerError =>
                new AiException(AiErrorKind.ProviderError, $"模型服务暂时不可用（HTTP {(int)status}）。"),

            _ => new AiException(AiErrorKind.ProviderError, $"模型调用失败（HTTP {(int)status}）：{detail}")
        };
    }

    private static AiResponse ParseResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new AiException(AiErrorKind.InvalidResponse, "模型返回里没有 choices。");
            }

            var content = choices[0].GetProperty("message").GetProperty("content");
            var text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(
                    string.Empty,
                    content.EnumerateArray().Select(part =>
                        part.TryGetProperty("text", out var partText)
                            ? partText.GetString()
                            : null)),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(text))
                throw new AiException(AiErrorKind.InvalidResponse, "模型返回了空内容。");

            int? promptTokens = null;
            int? completionTokens = null;

            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var p))
                    promptTokens = p;
                if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var c))
                    completionTokens = c;
            }

            return new AiResponse(text.Trim())
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            };
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.InvalidResponse, "模型返回不是合法 JSON。", ex);
        }
        catch (KeyNotFoundException ex)
        {
            throw new AiException(AiErrorKind.InvalidResponse, "模型返回缺少 message.content。", ex);
        }
    }
}
