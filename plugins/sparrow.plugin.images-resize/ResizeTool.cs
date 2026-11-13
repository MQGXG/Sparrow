using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using SkiaSharp;

namespace Sparrow.Plugin.Images.Resize;

/// <summary>
/// 图片缩放。
///
/// 两种模式：按最长边（把最长边缩到指定像素，保持比例）、按百分比。
/// 输出统一 PNG；要别的格式再接力「图片格式转换」。
/// </summary>
public sealed class ResizeTool : ITool
{
    private const string ParameterMode = "mode";
    private const string ParameterValue = "value";

    private const string ModeEdge = "按边长";
    private const string ModePercent = "按比例";

    private const int MinSize = 1;
    private const int MaxSize = 10000;

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.images.resize",
        Name = "图片缩放",
        Category = "images",
        Description = "按最长边或百分比缩放图片，输出 PNG",
        Keywords = ["图片", "缩放", "尺寸", "resize", "大小", "比例"],
        Accepts = InputShape.Image,
        Produces = OutputShape.Image,
        Requires = PlatformCapability.None,
        Order = 10,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterMode,
                Label = "模式",
                Kind = ParameterKind.Choice,
                Choices = [ModeEdge, ModePercent],
                Default = ModeEdge
            },
            new ToolParameter
            {
                Key = ParameterValue,
                Label = "数值",
                Kind = ParameterKind.Number,
                Default = "1024",
                Hint = "按边长：最长边的目标像素；按比例：百分数（100 为原大）"
            }
        ]
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not ImageInput input)
            return Task.FromResult(ToolResult.InvalidInput("需要图片输入"));

        if (input.ByteSize == 0)
            return Task.FromResult(ToolResult.InvalidInput("图片为空"));

        var value = context.GetParameter(ParameterValue, 1024);
        if (value <= 0)
            return Task.FromResult(ToolResult.InvalidInput("数值必须大于 0"));

        try
        {
            using var bitmap = Decode(input.Data);
            if (bitmap is null)
                return Task.FromResult(ToolResult.InvalidInput("图片解码失败，格式不受支持"));

            var (newWidth, newHeight) = ComputeSize(bitmap.Width, bitmap.Height, context.GetParameter(ParameterMode, ModeEdge), value);
            if (newWidth == bitmap.Width && newHeight == bitmap.Height)
                return Task.FromResult(ToolResult.Ok(new ImagePayload(input.Data, ImageFormat.Png)
                {
                    Width = bitmap.Width,
                    Height = bitmap.Height
                }));

            using var resized = bitmap.Resize(new SKSizeI(newWidth, newHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (resized is null)
                return Task.FromResult(ToolResult.Failed("缩放失败"));

            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null)
                return Task.FromResult(ToolResult.Failed("编码 PNG 失败"));

            return Task.FromResult(ToolResult.Ok(new ImagePayload(encoded.ToArray(), ImageFormat.Png)
            {
                Width = newWidth,
                Height = newHeight
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed("图片缩放失败", ex.Message));
        }
    }

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

    /// <summary>按模式算出目标尺寸，保持比例，夹在 1 到 10000 像素之间。</summary>
    private static (int Width, int Height) ComputeSize(int width, int height, string mode, int value)
    {
        if (mode == ModePercent)
        {
            var percent = Math.Clamp(value, 1, 1000) / 100.0;
            return (
                ClampSize((int)Math.Round(width * percent)),
                ClampSize((int)Math.Round(height * percent)));
        }

        // 按边长：最长边对齐目标值
        var longest = Math.Max(width, height);
        var scale = Math.Min(1.0 * MaxSize / longest, (double)value / longest);

        return (
            ClampSize((int)Math.Round(width * scale)),
            ClampSize((int)Math.Round(height * scale)));
    }

    private static int ClampSize(int size) => Math.Clamp(size, MinSize, MaxSize);
}
