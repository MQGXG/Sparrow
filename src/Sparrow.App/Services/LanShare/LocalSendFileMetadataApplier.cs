using Sparrow.App.Services.LanShare.Models;

namespace Sparrow.App.Services.LanShare;

/// <summary>Restores the optional file timestamps transferred in a LocalSend file metadata DTO.</summary>
internal static class LocalSendFileMetadataApplier
{
    internal static void Apply(string path, LocalSendFileMetadataDto? metadata)
    {
        if (metadata == null)
            return;

        try
        {
            if (metadata.LastModified.HasValue)
                File.SetLastWriteTimeUtc(path, metadata.LastModified.Value.ToUniversalTime());
            if (metadata.LastAccessed.HasValue)
                File.SetLastAccessTimeUtc(path, metadata.LastAccessed.Value.ToUniversalTime());
        }
        catch (Exception ex)
        {
            LanShareLog.Log($"[LocalSendServer] Failed to restore file timestamps for {path}: {ex.Message}", LogLevel.Debug);
        }
    }
}
