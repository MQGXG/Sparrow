using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using SkiaSharp;

namespace Sparrow.Plugin.Images.Convert;

/// <summary>
/// 图片格式转换。
///
/// 输出 ImagePayload（目标格式的字节），落盘交给宿主的「保存」动作——
/// 按契约插件不写盘。转完可以「复制」回剪贴板（宿主负责转回 PNG），也可以继续接力其他工具。
/// </summary>
public sealed class ConvertTool : ITool
{
    private const string ParameterFormat = "format";
    private const string ParameterQuality = "quality";

    private const string FormatPng = "PNG";
    private const string FormatJpeg = "JPEG";
    private const string FormatWebp = "WebP";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.images.convert",
        Name = "图片格式转换",
        Category = "images",
        Description = "把图片转成 PNG / JPEG / WebP，保存或复制走",
        Keywords = ["图片", "格式", "转换", "convert", "webp", "jpeg", "png", "压缩"],
        Accepts = InputShape.Image,
        Produces = OutputShape.Image,
        Requires = PlatformCapability.None,
        Order = 10,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterFormat,
                Label = "目标格式",
                Kind = ParameterKind.Choice,
                Choices = [FormatPng, FormatJpeg, FormatWebp],
                Default = FormatPng
            },
            new ToolParameter
            {
                Key = ParameterQuality,
                Label = "质量",
                Kind = ParameterKind.Number,
                Default = "90",
                Hint = "1-100；仅 JPEG / WebP 生效"
            }
        ]
    };

    /// <summary>解码失败（含抛异常的坏字节）一律算 InvalidInput，不落 Execution。</summary>
    private static SKBitmap? Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            return SKBitmap.Decode(data.Span);
        }
        catch
        {
            return null;
        }
    }

    public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not ImageInput input)
            return Task.FromResult(ToolResult.InvalidInput("需要图片输入"));

        if (input.ByteSize == 0)
            return Task.FromResult(ToolResult.InvalidInput("图片为空"));

        var format = context.GetParameter(ParameterFormat, FormatPng);
        var quality = Math.Clamp(context.GetParameter(ParameterQuality, 90), 1, 100);

        try
        {
            using var bitmap = Decode(input.Data);
            if (bitmap is null)
                return Task.FromResult(ToolResult.InvalidInput("图片解码失败，格式不受支持"));

            var (encodedFormat, payloadFormat) = format switch
            {
                FormatJpeg => (SKEncodedImageFormat.Jpeg, ImageFormat.Jpeg),
                FormatWebp => (SKEncodedImageFormat.Webp, ImageFormat.Webp),
                _ => (SKEncodedImageFormat.Png, ImageFormat.Png)
            };

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(encodedFormat, quality);
            if (encoded is null)
                return Task.FromResult(ToolResult.Failed("目标格式编码失败"));

            return Task.FromResult(ToolResult.Ok(new ImagePayload(encoded.ToArray(), payloadFormat)
            {
                Width = bitmap.Width,
                Height = bitmap.Height
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed("图片转换失败", ex.Message));
        }
    }
}
