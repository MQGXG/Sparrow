using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 把剪贴板条目拖出成真实文件。
///
/// 文本 / 图片先落到临时目录再作为文件拖出——目标应用（资源管理器、聊天框、编辑器）
/// 收到的是文件，而不是我们自定义的格式。
/// </summary>
public static class ClipboardDragService
{
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

    public static async Task<IDataTransfer?> CreateAsync(
        ClipboardPastePayload payload,
        IStorageProvider storage)
    {
        var transfer = new DataTransfer();
        var added = false;

        switch (payload.Kind)
        {
            case ClipboardEntryKind.Text when payload.Text is { Length: > 0 } text:
                var textFile = await WriteTempFileAsync(".txt", text);
                added = AddFile(transfer, await storage.TryGetFileFromPathAsync(textFile));
                break;

            case ClipboardEntryKind.Image when payload.Png is { Length: > 0 } png:
                var imageFile = await WriteTempFileAsync(".png", png);
                added = AddFile(transfer, await storage.TryGetFileFromPathAsync(imageFile));
                break;

            case ClipboardEntryKind.Files:
                foreach (var path in payload.Paths)
                {
                    if (File.Exists(path) && AddFile(transfer, await storage.TryGetFileFromPathAsync(path)))
                        added = true;
                }
                break;
        }

        return added ? transfer : null;
    }

    private static bool AddFile(DataTransfer transfer, IStorageFile? file)
    {
        if (file is null)
            return false;

        transfer.Add(DataTransferItem.CreateFile(file));
        return true;
    }

    private static async Task<string> WriteTempFileAsync(string extension, string text) =>
        await WriteTempFileAsync(extension, System.Text.Encoding.UTF8.GetBytes(text));

    private static async Task<string> WriteTempFileAsync(string extension, byte[] bytes)
    {
        Directory.CreateDirectory(DragDirectory);

        var name = $"clip-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..32] + extension;
        var path = Path.Combine(DragDirectory, name);

        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
