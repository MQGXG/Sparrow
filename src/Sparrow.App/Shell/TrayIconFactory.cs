using Avalonia.Controls;
using Avalonia.Platform;

namespace Sparrow.App.Shell;

/// <summary>
/// 托盘图标。
/// 从 Avalonia 资源读取，和 exe/窗口图标共用同一个 PNG，
/// 避免再维护一份 base64 字符串。
/// </summary>
internal static class TrayIconFactory
{
    private static readonly Uri Source = new("avares://Sparrow.App/Assets/sparrow-32.png");

    public static WindowIcon Create()
    {
        using var stream = AssetLoader.Open(Source);
        return new WindowIcon(stream);
    }
}
