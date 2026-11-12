using Microsoft.Extensions.Logging;

namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 宿主注入给插件的全部外部能力。
///
/// 只有三样：日志、设置、受控的网络。
/// **没有文件系统、没有剪贴板、没有数据库、没有窗口**——
/// 需要产出文件时返回 FileSetPayload，由宿主落盘；
/// 需要读取剪贴板时，输入已经在 <see cref="ToolContext.Input"/> 里了。
/// </summary>
public interface IToolHost
{
    ILogger Logger { get; }

    /// <summary>按 toolId 隔离的键值存储，写入 settings.json 的 plugins 节点下。</summary>
    IToolSettings Settings { get; }

    /// <summary>受用户策略控制的网络访问。禁用时 IsAvailable 为 false。</summary>
    IHttpClientAccess Http { get; }

    /// <summary>
    /// 插件自己的安装目录（只读）。
    ///
    /// 用于定位随插件分发的资源，比如 OCR 的 worker exe 与模型文件。
    /// 插件不得用它读写用户文件——那是宿主的事。
    /// 这是「进程插件」唯一需要宿主额外提供的东西：它得知道自己在哪。
    /// </summary>
    string PluginDirectory { get; }
}

/// <summary>
/// 插件私有的设置存储。宿主保证不同插件的键互不可见。
/// </summary>
public interface IToolSettings
{
    string? Get(string key);

    void Set(string key, string? value);

    int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var value) ? value : fallback;

    bool GetBool(string key, bool fallback) =>
        bool.TryParse(Get(key), out var value) ? value : fallback;
}

/// <summary>
/// 受控的 HTTP 访问。
/// 插件不得自己 new HttpClient：连接池、超时、代理、用户同意策略都由宿主统一管理。
/// </summary>
public interface IHttpClientAccess
{
    /// <summary>用户是否允许联网。为 false 时插件应返回 Unsupported，而不是静默失败。</summary>
    bool IsAvailable { get; }

    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken);
}
