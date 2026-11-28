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
public sealed class ToolHost : IToolHost, IDisposable
{
    private readonly List<Action> _disposers = [];
    private bool _disposed;

    public ToolHost(
        string pluginId,
        string pluginDirectory,
        IAppLog log,
        IAiClient? ai = null,
        IClipboardHistory? clipboardHistory = null,
        IWindowOverlay? windows = null,
        ILocalModels? localModels = null,
        ILocalTranslation? localTranslation = null,
        IToolSettings? settings = null)
    {
        PluginDirectory = pluginDirectory;
        Logger = new PluginLogger(pluginId, log);
        Settings = settings ?? new InMemoryToolSettings();
        Http = DisabledHttpAccess.Instance;
        Ai = ai ?? NullAiClient.Instance;
        LocalModels = localModels ?? NullLocalModels.Instance;
        LocalTranslation = localTranslation ?? NullLocalTranslation.Instance;
        ClipboardHistory = clipboardHistory;
        Windows = windows;
    }

    public ILogger Logger { get; }

    public void OnDispose(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_disposers)
        {
            if (!_disposed)
            {
                _disposers.Add(callback);
                return;
            }
        }

        // 已经卸载：立即清理，避免调用方以为登记成功
        InvokeSafe(callback);
    }

    public IToolSettings Settings { get; }

    public IHttpClientAccess Http { get; }

    public IAiClient Ai { get; }

    /// <summary>本地模型：宿主管下载与缓存，插件只拿文件路径。</summary>
    public ILocalModels LocalModels { get; }

    /// <summary>本地翻译引擎：多个插件共用同一份提示词与推理实现。</summary>
    public ILocalTranslation LocalTranslation { get; }

    public string PluginDirectory { get; }

    /// <summary>宿主的剪贴板历史；未开启记录或测试场景为 null，插件必须 null 检查。</summary>
    public IClipboardHistory? ClipboardHistory { get; }

    /// <summary>通用窗口覆盖能力；无 UI 的测试环境为 null。</summary>
    public IWindowOverlay? Windows { get; }

    public void Dispose()
    {
        Action[] snapshot;

        lock (_disposers)
        {
            if (_disposed)
                return;

            _disposed = true;
            snapshot = _disposers.ToArray();
            _disposers.Clear();
        }

        // 后注册的先执行：与常见的资源栈语义一致
        for (var i = snapshot.Length - 1; i >= 0; i--)
            InvokeSafe(snapshot[i]);
    }

    private void InvokeSafe(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            // 一个清理回调失败不能阻止其他回调，也不能把卸载路径打断
            Logger.LogError(ex, "插件清理回调执行失败");
        }
    }
}

/// <summary>
/// 无宿主场景（单元测试、直接 new 工具）的空实现。
/// 所有能力都退化为「什么都不做」，插件必须能接受这种环境。
/// </summary>
public sealed class NullToolHost : IToolHost
{
    public static NullToolHost Instance { get; } = new();

    public ILogger Logger => NullLogger.Instance;

    public void OnDispose(Action callback) { }

    public IToolSettings Settings { get; } = new InMemoryToolSettings();

    public IHttpClientAccess Http => DisabledHttpAccess.Instance;

    public IAiClient Ai => NullAiClient.Instance;

    public ILocalModels LocalModels => NullLocalModels.Instance;

    public ILocalTranslation LocalTranslation => NullLocalTranslation.Instance;

    public string PluginDirectory => AppContext.BaseDirectory;

    public IClipboardHistory? ClipboardHistory => null;

    public IWindowOverlay? Windows => null;
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
