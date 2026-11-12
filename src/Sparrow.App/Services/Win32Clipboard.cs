using System.Runtime.InteropServices;
using SkiaSharp;

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
    private const uint CfDib = 8;
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

    public byte[]? GetImagePng()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            var handle = GetClipboardData(CfDib);
            if (handle == IntPtr.Zero)
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                var size = (int)GlobalSize(handle);
                if (size <= 0)
                    return null;

                var dib = new byte[size];
                Marshal.Copy(pointer, dib, 0, size);
                return DibToPng(dib);
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

    /// <summary>
    /// CF_DIB → PNG。
    /// 剪贴板里的 DIB 没有 BMP 文件头（那 14 字节只在 .bmp 文件里存在），
    /// 补上之后 SkiaSharp 才能解码。
    /// </summary>
    internal static byte[]? DibToPng(byte[] dib)
    {
        if (dib.Length < 40)
            return null;

        try
        {
            var headerSize = BitConverter.ToInt32(dib, 0);
            if (headerSize < 40 || headerSize > dib.Length)
                return null;

            var bitCount = BitConverter.ToInt16(dib, 14);
            var colorsUsed = BitConverter.ToInt32(dib, 32);
            var paletteEntries = colorsUsed != 0
                ? colorsUsed
                : bitCount <= 8 ? 1 << bitCount : 0;
            var pixelOffset = 14 + headerSize + paletteEntries * 4;

            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.TryWriteBytes(bmp.AsSpan(2, 4), bmp.Length);
            BitConverter.TryWriteBytes(bmp.AsSpan(10, 4), pixelOffset);
            Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);

            using var bitmap = SKBitmap.Decode(bmp);
            if (bitmap is null)
                return null;

            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
        catch
        {
            // 剪贴板图片格式五花八门，解不了就当没有图片，不抛给调用方
            return null;
        }
    }

    public bool SetText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            // +1 是结尾的 UTF-16 空字符
            var byteCount = (text.Length + 1) * sizeof(char);
            var handle = GlobalAlloc(GmemMoveable, (nuint)byteCount);
            if (handle == IntPtr.Zero)
                return false;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
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

            // 成功后所有权移交给系统，不能自己 GlobalFree；失败则自己释放
            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
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
    private static extern nuint GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
