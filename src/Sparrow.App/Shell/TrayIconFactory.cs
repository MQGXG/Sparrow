using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Sparrow.App.Shell;

/// <summary>
/// 托盘图标。
/// 和 exe/窗口图标共用同一个 PNG 资源。
///
/// 这里必须先把流解码成 Bitmap 再交给 WindowIcon：
/// WindowIcon 允许延迟读取，如果流被 dispose，HICON 创建会失败，
/// 表现就是「进程正常、日志正常，但托盘里没有图标」。
/// </summary>
internal static class TrayIconFactory
{
    private static readonly Uri Source = new("avares://Sparrow.App/Assets/sparrow-32.png");

    public static WindowIcon Create()
    {
        using var stream = AssetLoader.Open(Source);
        using var bitmap = new Bitmap(stream);
        return new WindowIcon(bitmap);
    }
}
