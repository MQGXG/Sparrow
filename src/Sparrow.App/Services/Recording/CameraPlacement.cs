using Avalonia;

namespace Sparrow.App.Services.Recording;

/// <summary>摄像头画中画的默认/位置换算工具。</summary>
internal static class CameraPlacement
{
    /// <summary>默认放右下角，高度约为录制区域的 1/4，保持 16:9。</summary>
    public static PixelRect DefaultScreenRect(PixelRect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return default;

        var margin = Math.Max(12, region.Height / 40);
        var maxWidth = Math.Max(1, region.Width - margin * 2);
        var maxHeight = Math.Max(1, region.Height - margin * 2);

        var height = Math.Min(maxHeight, Math.Max(96, region.Height / 4));
        var width = Math.Min(maxWidth, height * 16 / 9);
        height = Math.Min(maxHeight, width * 9 / 16);

        return new PixelRect(
            region.Right - width - margin,
            region.Bottom - height - margin,
            width,
            height);
    }
}
