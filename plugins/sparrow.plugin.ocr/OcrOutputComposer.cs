using System.Text.Json;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Ocr;

/// <summary>
/// 把 worker 的原始输出整理成 <see cref="OcrResultPayload"/>。
///
/// 单张与批量共用：批量只是对每张图调用一次，再按文件拼成全文。
/// 兼容旧版纯文本输出（不以 { 开头时原样返回，行框为空）。
/// </summary>
public static class OcrOutputComposer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static OcrResultPayload Compose(string output, OcrLayoutMode mode)
    {
        var trimmed = output.Trim();

        if (!trimmed.StartsWith('{'))
            return new OcrResultPayload(trimmed, Array.Empty<OcrTextLine>());

        try
        {
            var payload = JsonSerializer.Deserialize<OcrWorkerResult>(trimmed, JsonOptions);
            if (payload is null)
                return new OcrResultPayload(trimmed, Array.Empty<OcrTextLine>());

            var lines = OcrTextComposer.ComposeLines(payload.Regions);
            var text = OcrTextComposer.ComposeText(payload.Regions, mode, payload.Text);

            // 坐标拼不出来时回退到 worker 的原始全文
            if (string.IsNullOrWhiteSpace(text))
                text = payload.Text;

            return new OcrResultPayload(text, lines);
        }
        catch (JsonException)
        {
            return new OcrResultPayload(trimmed, Array.Empty<OcrTextLine>());
        }
    }

    /// <summary>worker 的错误帧：<c>{"error":"..."}</c>。不是错误帧返回 null。</summary>
    public static string? TryReadError(string output)
    {
        var trimmed = output.Trim();
        if (!trimmed.StartsWith('{'))
            return null;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() ?? "worker 返回错误"
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
