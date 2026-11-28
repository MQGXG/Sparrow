using Microsoft.Extensions.Logging;

namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 宿主注入给插件的全部外部能力。
///
/// 能力清单：日志、设置、受控网络、AI、本地模型、本地翻译、剪贴板历史、插件目录、
/// 窗口覆盖，以及一个卸载清理登记入口。
/// **没有文件系统、没有实时剪贴板、没有数据库、没有窗口**——
/// 需要产出文件时返回 FileSetPayload，由宿主落盘；
/// 需要读取剪贴板时，输入已经在 <see cref="ToolContext.Input"/> 里了；
/// 需要历史剪贴板时走 <see cref="ClipboardHistory"/>（宿主监听 + 落盘，插件只读）。
/// </summary>
public interface IToolHost
{
    ILogger Logger { get; }

    /// <summary>
    /// 注册一个卸载时执行的清理动作；后注册的先执行。
    /// 用于 Timer、事件订阅、原生句柄等宿主无法自动回收的副作用。
    /// 卸载后迟到的注册会立即执行，避免调用方误以为登记成功。
    /// </summary>
    void OnDispose(Action callback);

    /// <summary>按插件隔离的键值存储，写入 settings.json 的 plugins 节点下。</summary>
    IToolSettings Settings { get; }

    /// <summary>
    /// 宿主记录的剪贴板历史。用户关闭记录（隐私）或测试场景下为 null，
    /// 插件必须 null 检查并给出「先在设置里开启」之类的提示。
    /// </summary>
    IClipboardHistory? ClipboardHistory { get; }

    /// <summary>受用户策略控制的网络访问。禁用时 IsAvailable 为 false。</summary>
    IHttpClientAccess Http { get; }

    /// <summary>
    /// 模型能力。密钥、端点、provider 都由宿主管理；
    /// 未配置时 IsAvailable 为 false。
    /// </summary>
    IAiClient Ai { get; }

    /// <summary>
    /// 本地模型（GGUF）。宿主管下载、校验、缓存与进度，插件只拿文件路径。
    /// 宿主未注入时是 <see cref="NullLocalModels"/>。
    /// </summary>
    ILocalModels LocalModels { get; }

    /// <summary>
    /// 本地翻译引擎：模型加载、提示词模板、采样与分块都在宿主，
    /// 多个插件（翻译、OCR）共用同一份实现。未注入时是 <see cref="NullLocalTranslation"/>。
    /// </summary>
    ILocalTranslation LocalTranslation { get; }

    /// <summary>
    /// 插件自己的安装目录（只读）。
    ///
    /// 用于定位随插件分发的资源，比如 OCR 的 worker exe 与模型文件。
    /// 插件不得用它读写用户文件——那是宿主的事。
    /// 这是「进程插件」唯一需要宿主额外提供的东西：它得知道自己在哪。
    /// </summary>
    string PluginDirectory { get; }

    /// <summary>
    /// 通用的窗口覆盖能力：拾取窗口 + 图钉指示。
    /// 无宿主环境（单元测试）为 null，插件必须判空并返回可读的提示。
    /// </summary>
    IWindowOverlay? Windows { get; }
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
