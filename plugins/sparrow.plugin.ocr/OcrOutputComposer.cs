using System.Text.Json;
using Sparrow.Plugin.Sdk;
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

    public static OcrResultPayload Compose(
        string output,
        OcrLayoutMode mode,
        string? formatting = null,
        string? punctuation = null)
    {
        var trimmed = output.Trim();

        if (!trimmed.StartsWith('{'))
            return new OcrResultPayload(trimmed, Array.Empty<OcrTextLine>());

        try
        {
            var payload = JsonSerializer.Deserialize<OcrWorkerResult>(trimmed, JsonOptions);
            if (payload is null)
                return new OcrResultPayload(trimmed, Array.Empty<OcrTextLine>());

            // 表格形状时保留 region 粒度（一格一行），否则合并成阅读行。
            // 粒度决定三件事：点选复制一行的范围、字符选择的锚点、译文落点。
            var lines = OcrTextComposer.LooksLikeTable(payload.Regions)
                ? OcrTextComposer.ComposeRegionLines(payload.Regions)
                : OcrTextComposer.ComposeLines(payload.Regions);

            // 文本区仍按整行合成：复制出去的是「一行表格」而不是竖排的单元格
            var text = OcrTextComposer.ComposeText(payload.Regions, mode, payload.Text);

            // 坐标拼不出来时回退到 worker 的原始全文
            if (string.IsNullOrWhiteSpace(text))
                text = payload.Text;

            // 格式 / 标点只改「复制出去的文本」，行框保持原样
            text = OcrTextTransform.Apply(text, formatting, punctuation);

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

    /// <summary>表格帧：<c>{"html":"..."}</c>。不是表格帧返回 null。</summary>
    public static string? TryReadTable(string output)
    {
        var trimmed = output.Trim();
        if (!trimmed.StartsWith('{'))
            return null;

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (!document.RootElement.TryGetProperty("html", out var html))
                return null;

            var text = html.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
