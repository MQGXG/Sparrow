using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace Sparrow.App.Services;

/// <summary>
/// 文件类型图标：用 Shell 的 SHGetFileInfo 取系统图标，转成 Avalonia Bitmap。
///
/// 结果按扩展名缓存；.lnk/.exe/.ico 按完整路径缓存，因为它们的图标取决于目标。
/// 调用方应在后台线程调用，缓存未命中时单次约 1–3 ms。
/// </summary>
internal static class ShellIconProvider
{
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    private static readonly ConcurrentDictionary<string, Bitmap> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取文件/文件夹图标；失败返回 null，由 UI 回退到几何占位图标。</summary>
    public static Bitmap? GetIcon(string path, bool isDirectory)
    {
        var key = CacheKey(path, isDirectory);

        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var icon = Load(path, isDirectory);
        if (icon is not null)
            Cache[key] = icon;

        return icon;
    }

    private static string CacheKey(string path, bool isDirectory)
    {
        if (isDirectory)
            return "<dir>";

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return "<file>";

        // 这些类型的图标取自目标文件，不能只按扩展名缓存
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return path.ToLowerInvariant();
        }

        return extension.ToLowerInvariant();
    }

    private static Bitmap? Load(string path, bool isDirectory)
    {
        try
        {
            var info = new ShFileInfo();
            var flags = ShgfiIcon | ShgfiLargeIcon;
            var attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;

            // 索引可能比磁盘慢半拍：文件已删除时仍能按扩展名给出类型图标
            if (!File.Exists(path) && !Directory.Exists(path))
                flags |= ShgfiUseFileAttributes;

            var result = SHGetFileInfo(
                path,
                attributes,
                ref info,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                flags);

            if (result == IntPtr.Zero || info.HIcon == IntPtr.Zero)
                return null;

            try
            {
                using var icon = System.Drawing.Icon.FromHandle(info.HIcon);
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();

                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;

                return new Bitmap(stream);
            }
            finally
            {
                DestroyIcon(info.HIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr HIcon;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShFileInfo info,
        uint infoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
