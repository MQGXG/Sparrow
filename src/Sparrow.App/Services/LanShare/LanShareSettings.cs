namespace Sparrow.App.Services.LanShare;

/// <summary>
/// 局域网互传设置。
///
/// 由宿主持久化到 Sparrow 的设置文件；协议实现只读这个模型。
/// 来源：Lertaro 的 LocalSendSettingsModel（MIT），搬到 Sparrow 后去掉主程序耦合。
/// </summary>
public sealed class LocalSendSettingsModel
{
    /// <summary>是否在 App 启动后常驻监听（接收文件）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>局域网内显示的名字，默认用机器名。</summary>
    public string DeviceAlias { get; set; } = Environment.MachineName;

    public int Port { get; set; } = 53317;

    /// <summary>免确认接收；false 时每个上传都要用户点确认。</summary>
    public bool QuickSave { get; set; }

    public bool QuickSaveFromFavorites { get; set; } = true;

    /// <summary>
    /// 默认接收目录用 shell 令牌，跟随系统重定向（例如 Downloads 被改到别的盘）。
    /// </summary>
    public const string DefaultDownloadDirectory = "shell:Downloads";

    public string DownloadDirectory { get; set; } = DefaultDownloadDirectory;

    public bool EnableHttps { get; set; } = true;

    public bool CreateChecksums { get; set; } = true;

    public bool VerifyChecksums { get; set; } = true;

    /// <summary>接收 PIN；为空表示不校验。</summary>
    public string? ReceivePin { get; set; }

    /// <summary>让发送方拉起本机发送窗口的一次性令牌。</summary>
    public string ShowToken { get; set; } = string.Empty;

    public int DiscoveryTimeout { get; set; } = 500;

    /// <summary>本机设备指纹；首次启动时由证书生成。</summary>
    public string DeviceFingerprint { get; set; } = string.Empty;
}
