using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.Views;

/// <summary>
/// 截图 OCR 的宿主结果。
/// <see cref="Lines"/> 为空表示插件只返回了文本（旧契约），
/// 窗口应关掉并回退到命令面板展示。
/// </summary>
public sealed record CaptureOcrResult(
    string Text,
    IReadOnlyList<OcrTextLine> Lines,
    string? Error = null)
{
    public bool IsFallback => Lines.Count == 0;

    public static CaptureOcrResult Failed(string error) =>
        new(string.Empty, Array.Empty<OcrTextLine>(), error);
}
