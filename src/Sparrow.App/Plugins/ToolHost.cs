using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Plugins;

/// <summary>
/// 宿主注入给插件的真实能力。
///
/// 每个插件一个实例，<see cref="PluginDirectory"/> 指向它自己的目录。
/// 网络默认关闭（本地优先），需要联网的插件必须先看 <c>Http.IsAvailable</c>。
/// </summary>
public sealed class ToolHost : IToolHost
{
    public ToolHost(string pluginId, string pluginDirectory, IAppLog log, IAiClient? ai = null)
    {
        PluginDirectory = pluginDirectory;
        Logger = new PluginLogger(pluginId, log);
        Settings = new InMemoryToolSettings();
        Http = DisabledHttpAccess.Instance;
        Ai = ai ?? NullAiClient.Instance;
    }

    public ILogger Logger { get; }

    public IToolSettings Settings { get; }

    public IHttpClientAccess Http { get; }

    public IAiClient Ai { get; }

    public string PluginDirectory { get; }
}

/// <summary>
/// 无宿主场景（单元测试、直接 new 工具）的空实现。
/// 所有能力都退化为「什么都不做」，插件必须能接受这种环境。
/// </summary>
public sealed class NullToolHost : IToolHost
{
    public static NullToolHost Instance { get; } = new();

    public ILogger Logger => NullLogger.Instance;

    public IToolSettings Settings { get; } = new InMemoryToolSettings();

    public IHttpClientAccess Http => DisabledHttpAccess.Instance;

    public IAiClient Ai => NullAiClient.Instance;

    public string PluginDirectory => AppContext.BaseDirectory;
}

/// <summary>把插件的 ILogger 调用转发到应用日志，并带上插件 ID 前缀。</summary>
internal sealed class PluginLogger : ILogger
{
    private readonly string _pluginId;
    private readonly IAppLog _log;

    public PluginLogger(string pluginId, IAppLog log)
    {
        _pluginId = pluginId;
        _log = log;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = $"[{_pluginId}] {formatter(state, exception)}";

        if (logLevel >= LogLevel.Error)
            _log.Error(message, exception);
        else if (logLevel >= LogLevel.Warning)
            _log.Warn(message);
        else
            _log.Info(message);
    }
}

/// <summary>
/// 插件私有的内存键值存储。
/// M1 不落盘：插件设置的重要性还不足以引入一轮 schema 迁移。
/// </summary>
internal sealed class InMemoryToolSettings : IToolSettings
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string? value)
    {
        if (value is null)
            _values.TryRemove(key, out _);
        else
            _values[key] = value;
    }
}

/// <summary>
/// 网络关闭时的实现。
/// 插件必须先检查 <see cref="IsAvailable"/>；直接调用是编程错误，所以抛异常而不是静默失败。
/// </summary>
internal sealed class DisabledHttpAccess : IHttpClientAccess
{
    public static DisabledHttpAccess Instance { get; } = new();

    public bool IsAvailable => false;

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "网络访问未启用。插件应先检查 IToolHost.Http.IsAvailable。");
}

/// <summary>
/// 未配置模型时的空实现。
/// 插件应先检查 <see cref="IsAvailable"/>；直接调用属于编程错误，抛异常而不是静默返回空。
/// </summary>
internal sealed class NullAiClient : IAiClient
{
    public static NullAiClient Instance { get; } = new();

    public bool IsAvailable => false;

    public string? ModelName => null;

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken) =>
        throw new AiException(
            AiErrorKind.NotConfigured,
            "未配置 AI 模型。插件应先检查 IToolHost.Ai.IsAvailable。");
}
