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

    private readonly IAppLog? _log;

    public Win32Clipboard(IAppLog? log = null) => _log = log;

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
        {
            _log?.Warn($"打开剪贴板失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            return null;
        }

        try
        {
            var handle = GetClipboardData(CfDib);
            if (handle == IntPtr.Zero)
            {
                _log?.Warn("剪贴板里没有 CF_DIB 格式");
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                _log?.Warn("GlobalLock 剪贴板图像失败");
                return null;
            }

            try
            {
                var size = (int)GlobalSize(handle);
                if (size <= 0)
                {
                    _log?.Warn("GlobalSize 返回 0");
                    return null;
                }

                var dib = new byte[size];
                Marshal.Copy(pointer, dib, 0, size);

                var png = DibToPng(dib);
                if (png is null)
                    _log?.Warn($"DIB 转 PNG 失败（DIB {size} 字节）");

                return png;
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
            var width = BitConverter.ToInt32(dib, 4);
            var height = Math.Abs(BitConverter.ToInt32(dib, 8));
            var compression = BitConverter.ToInt32(dib, 16);
            var colorsUsed = BitConverter.ToInt32(dib, 32);
            var paletteEntries = colorsUsed != 0
                ? colorsUsed
                : bitCount <= 8 ? 1 << bitCount : 0;

            // BITMAPINFOHEADER（40 字节）+ BI_BITFIELDS 时，header 后面还有
            // 3 个 DWORD 的 RGB masks；漏掉这 12 字节会把 masks 当成像素数据
            var bitfieldMasks = compression == 3 && headerSize == 40 ? 12 : 0;
            var pixelOffset = 14 + headerSize + bitfieldMasks + paletteEntries * 4;

            // CF_DIB 的内存块可能比实际像素数据多几个字节填充；
            // bfSize 必须按位图真实大小算，否则解码器会拒绝
            var stride = ((width * bitCount + 31) / 32) * 4;
            var pixelBytes = stride * height;
            var fileSize = pixelOffset + pixelBytes;

            var bmp = new byte[fileSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.TryWriteBytes(bmp.AsSpan(2, 4), fileSize);
            BitConverter.TryWriteBytes(bmp.AsSpan(10, 4), pixelOffset);

            var copyLength = Math.Min(dib.Length, fileSize - 14);
            Buffer.BlockCopy(dib, 0, bmp, 14, copyLength);

            // 32bpp 的 DIB 常带 alpha=0；不修正的话转成 PNG 会整张透明，
            // OCR 读到的是空白图
            if (bitCount == 32)
            {
                for (var offset = pixelOffset + 3; offset < bmp.Length; offset += 4)
                {
                    if (bmp[offset] == 0)
                        bmp[offset] = 0xFF;
                }
            }

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

    /// <summary>把 PNG 写入剪贴板（转成 CF_DIB）。返回 false 表示失败。</summary>
    public bool SetImagePng(byte[] png)
    {
        if (png.Length == 0)
            return false;

        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
            return false;

        try
        {
            var width = bitmap.Width;
            var height = bitmap.Height;
            var stride = width * 4;
            var pixelBytes = stride * height;

            // CF_DIB：40 字节 BITMAPINFOHEADER + 32bpp 像素（top-down）
            var dib = new byte[40 + pixelBytes];
            BitConverter.TryWriteBytes(dib.AsSpan(0, 4), 40);
            BitConverter.TryWriteBytes(dib.AsSpan(4, 4), width);
            BitConverter.TryWriteBytes(dib.AsSpan(8, 4), -height);
            BitConverter.TryWriteBytes(dib.AsSpan(12, 2), (short)1);
            BitConverter.TryWriteBytes(dib.AsSpan(14, 2), (short)32);
            BitConverter.TryWriteBytes(dib.AsSpan(20, 4), pixelBytes);

            var pixels = bitmap.GetPixelSpan();
            var copyLength = Math.Min(pixels.Length, pixelBytes);
            pixels[..copyLength].CopyTo(dib.AsSpan(40));

            if (!OpenClipboard(IntPtr.Zero))
                return false;

            try
            {
                if (!EmptyClipboard())
                    return false;

                var handle = GlobalAlloc(GmemMoveable, (nuint)dib.Length);
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
                    Marshal.Copy(dib, 0, pointer, dib.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CfDib, handle) == IntPtr.Zero)
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
        finally
        {
            bitmap.Dispose();
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
