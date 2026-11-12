using System.Runtime.InteropServices;
using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 真实 Win32 剪贴板的往返测试。
///
/// 这一层之前只有 FakeClipboard 覆盖，导致「复制静默失败」在真机上才暴露。
/// 测试会临时改写系统剪贴板，结束后恢复原内容。
/// </summary>
public sealed class Win32ClipboardTests
{
    [Fact]
    public void 写入后能原样读回()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var clipboard = new Win32Clipboard();
        var original = clipboard.GetText();
        var probe = "sparrow-clipboard-" + Guid.NewGuid().ToString("N");

        try
        {
            clipboard.SetText(probe).ShouldBeTrue();
            clipboard.GetText().ShouldBe(probe);
        }
        finally
        {
            if (original is not null)
                clipboard.SetText(original);
        }
    }

    [Fact]
    public void 写入空字符串后能读回空字符串()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var clipboard = new Win32Clipboard();
        var original = clipboard.GetText();

        try
        {
            clipboard.SetText(string.Empty).ShouldBeTrue();

            // 空字符串是合法内容，不应该被当成「没有文本」
            clipboard.GetText().ShouldBe(string.Empty);
        }
        finally
        {
            if (original is not null)
                clipboard.SetText(original);
        }
    }

    [Fact]
    public void DIB转PNG_生成合法PNG()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dib = CreateDib(4, 4);

        var png = Win32Clipboard.DibToPng(dib);

        png.ShouldNotBeNull();

        // PNG 魔数：89 50 4E 47
        png![0].ShouldBe((byte)0x89);
        png[1].ShouldBe((byte)0x50);
        png[2].ShouldBe((byte)0x4E);
        png[3].ShouldBe((byte)0x47);
    }

    [Fact]
    public void 剪贴板里的DIB_能读成PNG()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var clipboard = new Win32Clipboard();
        var originalText = clipboard.GetText();

        try
        {
            SetClipboardDib(CreateDib(8, 8));

            var png = clipboard.GetImagePng();

            png.ShouldNotBeNull();

            // 按 PNG 魔数判断，而不是「非空」
            png![0].ShouldBe((byte)0x89);
            png[1].ShouldBe((byte)0x50);
        }
        finally
        {
            if (originalText is not null)
                clipboard.SetText(originalText);
        }
    }

    /// <summary>构造一张全白 32bpp 的 CF_DIB（没有 BMP 文件头）。</summary>
    private static byte[] CreateDib(int width, int height)
    {
        var stride = width * 4;
        var pixelBytes = stride * height;
        var dib = new byte[40 + pixelBytes];

        BitConverter.TryWriteBytes(dib.AsSpan(0, 4), 40);          // biSize
        BitConverter.TryWriteBytes(dib.AsSpan(4, 4), width);       // biWidth
        BitConverter.TryWriteBytes(dib.AsSpan(8, 4), height);      // biHeight
        BitConverter.TryWriteBytes(dib.AsSpan(12, 2), (short)1);   // biPlanes
        BitConverter.TryWriteBytes(dib.AsSpan(14, 2), (short)32);  // biBitCount
        BitConverter.TryWriteBytes(dib.AsSpan(20, 4), pixelBytes); // biSizeImage

        for (var offset = 40; offset < dib.Length; offset += 4)
        {
            dib[offset] = 0xFF;     // B
            dib[offset + 1] = 0xFF; // G
            dib[offset + 2] = 0xFF; // R
            dib[offset + 3] = 0xFF; // A
        }

        return dib;
    }

    /// <summary>把 DIB 放进系统剪贴板（CF_DIB）。</summary>
    private static void SetClipboardDib(byte[] dib)
    {
        if (!OpenClipboard(IntPtr.Zero))
            throw new InvalidOperationException("无法打开剪贴板");

        try
        {
            EmptyClipboard();

            var handle = GlobalAlloc(GmemMoveable, (nuint)dib.Length);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("GlobalAlloc 失败");

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("GlobalLock 失败");

            try
            {
                Marshal.Copy(dib, 0, pointer, dib.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfDib, handle) == IntPtr.Zero)
                throw new InvalidOperationException("SetClipboardData 失败");
        }
        finally
        {
            CloseClipboard();
        }
    }

    private const uint CfDib = 8;
    private const uint GmemMoveable = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
