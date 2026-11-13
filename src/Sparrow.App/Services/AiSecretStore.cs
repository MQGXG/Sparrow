using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Sparrow.App.Services;

/// <summary>
/// AI API Key 的本地加密存储。
///
/// 用 Windows DPAPI（当前用户范围）加密，文件放在
/// <c>%LOCALAPPDATA%\Sparrow\ai-key.dat</c>。
/// Key 不进入 settings.json、不进日志、不传给插件。
/// </summary>
public sealed class AiSecretStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sparrow.AiKey.v1");

    private readonly string _path;
    private readonly IAppLog _log;

    public AiSecretStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
    }

    public string? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_path))
            return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var plain = Unprotect(protectedBytes);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // 换了机器/用户后 DPAPI 解不开：当作没有配置，不阻塞启动
            _log.Warn($"读取 AI 密钥失败：{ex.Message}");
            return null;
        }
    }

    public bool Save(string? apiKey)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (File.Exists(_path))
                    File.Delete(_path);
                return true;
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var protectedBytes = Protect(Encoding.UTF8.GetBytes(apiKey));
            File.WriteAllBytes(_path, protectedBytes);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("保存 AI 密钥失败", ex);
            return false;
        }
    }

    private static byte[] Protect(byte[] plain)
    {
        var input = CreateBlob(plain);
        var entropy = CreateBlob(Entropy);
        var output = new DataBlob();

        try
        {
            if (!CryptProtectData(
                    ref input,
                    "Sparrow AI Key",
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyBlob(output);
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
            FreeBlob(output);
        }
    }

    private static byte[] Unprotect(byte[] protectedBytes)
    {
        var input = CreateBlob(protectedBytes);
        var entropy = CreateBlob(Entropy);
        var output = new DataBlob();

        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyBlob(output);
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
            FreeBlob(output);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var blob = new DataBlob
        {
            Size = bytes.Length,
            Data = Marshal.AllocHGlobal(bytes.Length)
        };

        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero || blob.Size <= 0)
            return [];

        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
            Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);
}
