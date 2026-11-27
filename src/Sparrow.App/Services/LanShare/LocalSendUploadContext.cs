using Sparrow.App.Services.LanShare.Models;

namespace Sparrow.App.Services.LanShare;

/// <summary>Immutable session data captured before an upload starts, so receiver cancellation cannot invalidate an in-flight request.</summary>
internal sealed record LocalSendUploadContext(
    string SessionId,
    PrepareUploadRequestDto Request,
    LocalSendFileDto File,
    HashSet<string>? SelectedFileIds,
    string DownloadDirectory,
    CancellationToken SessionCancellation,
    LocalSendReceiveProgress Progress);
