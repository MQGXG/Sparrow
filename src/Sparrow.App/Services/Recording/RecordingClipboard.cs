using System.Runtime.InteropServices;
using System.Text;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 把录屏文件放进剪贴板（CF_HDROP），对齐 Snow-shot 的「复制录屏」：
/// 停止录制并保存后，资源管理器 / 聊天窗口里可以直接 Ctrl+V 粘贴成文件。
/// </summary>
internal static class RecordingClipboard
{
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;

    /// <summary>复制文件到剪贴板；文件不存在或系统拒绝时返回 false。</summary>
    public static bool CopyFile(string path) => CopyPaths([path]);

    /// <summary>复制多个文件/文件夹到剪贴板，资源管理器里可以直接粘贴。</summary>
    public static bool CopyPaths(IEnumerable<string> paths)
    {
        var existing = paths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path)
                && (File.Exists(path) || Directory.Exists(path)))
            .ToArray();

        if (existing.Length == 0)
            return false;

        byte[] payload;

        try
        {
            payload = BuildDropFiles(existing);
        }
        catch
        {
            return false;
        }

        // Avalonia 的 UI 线程是 STA，剪贴板不需要再切线程。
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            var handle = GlobalAlloc(GmemMoveable, (UIntPtr)payload.Length);

            if (handle == IntPtr.Zero)
                return false;

            var target = GlobalLock(handle);

            if (target == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }

            try
            {
                Marshal.Copy(payload, 0, target, payload.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            // 成功后所有权归系统，不能再 GlobalFree。
            if (SetClipboardData(CfHDrop, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>DROPFILES 头 + 双 null 结尾的 UTF-16 路径列表。</summary>
    private static byte[] BuildDropFiles(IReadOnlyList<string> paths)
    {
        const int headerSize = 20;
        var file = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var buffer = new byte[headerSize + file.Length];

        BitConverter.GetBytes(headerSize).CopyTo(buffer, 0); // pFiles：路径相对本结构的偏移
        BitConverter.GetBytes(0).CopyTo(buffer, 4);          // pt.x
        BitConverter.GetBytes(0).CopyTo(buffer, 8);          // pt.y
        BitConverter.GetBytes(0).CopyTo(buffer, 12);         // fNC
        BitConverter.GetBytes(1).CopyTo(buffer, 16);         // fWide = TRUE

        file.CopyTo(buffer, headerSize);
        return buffer;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
