using System.Runtime.InteropServices;

namespace Sparrow.App.Services;

/// <summary>
/// Win32 剪贴板实现。
///
/// 为什么不用 Avalonia 的 Clipboard：
/// 命令面板在「尚未显示」的时候就需要读剪贴板判断输入，
/// 而 Avalonia 的 Clipboard 依赖 TopLevel 的平台实现，未显示的窗口拿不到。
/// M0 只支持 Windows，直接用 Win32 更可靠；M1 再补跨平台实现。
/// </summary>
public sealed class Win32Clipboard : IClipboardAccess
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public string? GetText()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void SetText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return;

        try
        {
            EmptyClipboard();

            // +1 是结尾的 UTF-16 空字符
            var byteCount = (text.Length + 1) * sizeof(char);
            var handle = GlobalAlloc(GmemMoveable, (nuint)byteCount);
            if (handle == IntPtr.Zero)
                return;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            // 成功后所有权移交给系统，不能自己 GlobalFree
            SetClipboardData(CfUnicodeText, handle);
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hwndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
