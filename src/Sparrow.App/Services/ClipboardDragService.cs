using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>拖拽时提供哪些格式。目标应用按自己的优先级选择。</summary>
public enum ClipboardDragMode
{
    /// <summary>文本 / 位图 + 文件都给。</summary>
    All,

    /// <summary>只给文本 / 位图（拖进输入框得到内容，不产生文件）。</summary>
    InlineOnly,

    /// <summary>只给文件（拖到资源管理器 / 聊天框得到文件）。</summary>
    FileOnly
}

/// <summary>一次拖拽的数据与需要延迟释放的资源（位图）。</summary>
public sealed record ClipboardDragData(IDataTransfer Transfer, IDisposable? Lifetime = null);

/// <summary>
/// 把剪贴板条目拖出。
///
/// 同一个拖拽项里同时放多种格式：文本 + 临时 .txt、图片 + 临时 .png + 位图。
/// 记事本 / 输入框会取文本，资源管理器会取文件，图片应用会取位图——
/// 具体取哪个由目标应用决定，我们只负责都提供。
/// </summary>
public static class ClipboardDragService
{
    /// <summary>文件名最多保留多少个字符，太长的首行不适合当文件名。</summary>
    private const int MaxBaseNameLength = 40;

    private static string DragDirectory =>
        Path.Combine(Path.GetTempPath(), "Sparrow", "drag");

    /// <summary>清理一天前的拖出临时文件；应用启动时调用一次。</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(DragDirectory))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-1);

            foreach (var file in Directory.GetFiles(DragDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // 文件仍被目标应用占用：留给下次清理
                }
            }
        }
        catch
        {
            // 临时目录不可用不影响主流程
        }
    }

    public static async Task<ClipboardDragData?> CreateAsync(
        ClipboardPastePayload payload,
        IStorageProvider? storage,
        ClipboardDragMode mode = ClipboardDragMode.All)
    {
        return payload.Kind switch
        {
            ClipboardEntryKind.Text when payload.Text is { Length: > 0 } text =>
                await CreateTextAsync(text, storage, mode),

            ClipboardEntryKind.Image when payload.Png is { Length: > 0 } png =>
                await CreateImageAsync(png, storage, mode),

            ClipboardEntryKind.Files when payload.Paths.Count > 0 =>
                await CreateFilesAsync(payload.Paths, storage, mode),

            _ => null
        };
    }

    private static async Task<ClipboardDragData?> CreateTextAsync(
        string text,
        IStorageProvider? storage,
        ClipboardDragMode mode)
    {
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        var added = false;

        if (mode != ClipboardDragMode.FileOnly)
        {
            item.Set(DataFormat.Text, text);
            added = true;
        }

        if (mode != ClipboardDragMode.InlineOnly)
        {
            var baseName = BuildTextBaseName(text);
            var path = await WriteTempFileAsync(".txt", text, baseName);
            var file = storage is null ? null : await storage.TryGetFileFromPathAsync(path);

            if (file is not null)
            {
                item.Set(DataFormat.File, file);
                added = true;
            }
        }

        if (!added)
            return null;

        transfer.Add(item);
        return new ClipboardDragData(transfer);
    }

    private static async Task<ClipboardDragData?> CreateImageAsync(
        byte[] png,
        IStorageProvider? storage,
        ClipboardDragMode mode)
    {
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        Bitmap? bitmap = null;
        var added = false;

        if (mode != ClipboardDragMode.FileOnly)
        {
            try
            {
                bitmap = new Bitmap(new MemoryStream(png));
                item.Set(DataFormat.Bitmap, bitmap);
                added = true;
            }
            catch
            {
                // 解码失败就只给文件
            }
        }

        if (mode != ClipboardDragMode.InlineOnly)
        {
            var path = await WriteTempFileAsync(
                ".png",
                png,
                $"图片-{DateTime.Now:yyyyMMdd-HHmmss}");
            var file = storage is null ? null : await storage.TryGetFileFromPathAsync(path);

            if (file is not null)
            {
                item.Set(DataFormat.File, file);
                added = true;
            }
        }

        if (!added)
        {
            bitmap?.Dispose();
            return null;
        }

        transfer.Add(item);
        return new ClipboardDragData(transfer, bitmap);
    }

    /// <summary>
    /// 把一组文件/文件夹打包成拖拽数据。
    /// 文件夹用 TryGetFolderFromPathAsync，否则资源管理器会把它当成文件。
    /// </summary>
    public static async Task<ClipboardDragData?> CreateFilesAsync(
        IReadOnlyList<string> paths,
        IStorageProvider? storage,
        ClipboardDragMode mode = ClipboardDragMode.All)
    {
        // 文件条目没有「内联内容」形态，任何模式下都按文件拖
        if (storage is null)
            return null;

        var transfer = new DataTransfer();
        var added = false;

        foreach (var path in paths)
        {
            IStorageItem? item = null;

            if (File.Exists(path))
                item = await storage.TryGetFileFromPathAsync(path);
            else if (Directory.Exists(path))
                item = await storage.TryGetFolderFromPathAsync(path);

            if (item is null)
                continue;

            transfer.Add(DataTransferItem.CreateFile(item));
            added = true;
        }

        return added ? new ClipboardDragData(transfer) : null;
    }

    /// <summary>
    /// 文本首行作为文件名：去掉非法字符、压缩空白、截断。
    /// 纯函数便于测试。
    /// </summary>
    public static string BuildTextBaseName(string text)
    {
        var firstLine = text
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

        if (firstLine is null)
            return "剪贴板文本";

        // 先压缩空白，再替换非法字符；否则制表符会先变成下划线
        var collapsed = Regex.Replace(firstLine, @"\s+", " ").Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(collapsed
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim(' ', '.');

        if (cleaned.Length == 0)
            return "剪贴板文本";

        return cleaned.Length <= MaxBaseNameLength
            ? cleaned
            : cleaned[..MaxBaseNameLength];
    }

    private static async Task<string> WriteTempFileAsync(string extension, string text, string baseName) =>
        await WriteTempFileAsync(extension, System.Text.Encoding.UTF8.GetBytes(text), baseName);

    private static async Task<string> WriteTempFileAsync(string extension, byte[] bytes, string baseName)
    {
        Directory.CreateDirectory(DragDirectory);

        var name = Sanitize(baseName);
        var path = Path.Combine(DragDirectory, name + extension);
        var index = 2;

        // 重名时加序号，不覆盖其他拖拽的临时文件
        while (File.Exists(path))
        {
            path = Path.Combine(DragDirectory, $"{name}-{index}{extension}");
            index++;
        }

        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim(' ', '.');

        if (cleaned.Length == 0)
            cleaned = "clip";

        return cleaned.Length <= MaxBaseNameLength
            ? cleaned
            : cleaned[..MaxBaseNameLength];
    }
}
