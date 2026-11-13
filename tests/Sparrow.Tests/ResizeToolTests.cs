using Shouldly;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Images.Resize;
using Xunit;

namespace Sparrow.Tests;

public sealed class ResizeToolTests
{
    private static ToolContext Context(byte[] png, IReadOnlyDictionary<string, string>? parameters = null) =>
        new()
        {
            Input = new ImageInput(png, ImageFormat.Png),
            Parameters = parameters ?? new Dictionary<string, string>()
        };

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    [Fact]
    public async Task 非图片输入_返回InvalidInput()
    {
        var result = await new ResizeTool().ExecuteAsync(
            new ToolContext { Input = new TextInput("not image") },
            CancellationToken.None);

        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 空图片_返回InvalidInput()
    {
        var result = await new ResizeTool().ExecuteAsync(
            new ToolContext { Input = new ImageInput(ReadOnlyMemory<byte>.Empty, ImageFormat.Png) },
            CancellationToken.None);

        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 按边长_最长边对齐目标值且保持比例()
    {
        var png = CreatePng(100, 40);

        var result = await new ResizeTool().ExecuteAsync(
            Context(png, new Dictionary<string, string> { ["mode"] = "按边长", ["value"] = "50" }),
            CancellationToken.None);

        var payload = result.Payload.ShouldBeOfType<ImagePayload>();
        payload.Format.ShouldBe(ImageFormat.Png);
        payload.Width.ShouldBe(50);
        payload.Height.ShouldBe(20);
    }

    [Fact]
    public async Task 按比例_200是放大一倍()
    {
        var png = CreatePng(100, 40);

        var result = await new ResizeTool().ExecuteAsync(
            Context(png, new Dictionary<string, string> { ["mode"] = "按比例", ["value"] = "200" }),
            CancellationToken.None);

        var payload = result.Payload.ShouldBeOfType<ImagePayload>();
        payload.Width.ShouldBe(200);
        payload.Height.ShouldBe(80);
    }

    [Fact]
    public async Task 目标尺寸等于原尺寸_原样返回不重编码()
    {
        var png = CreatePng(100, 40);

        var result = await new ResizeTool().ExecuteAsync(
            Context(png, new Dictionary<string, string> { ["mode"] = "按边长", ["value"] = "100" }),
            CancellationToken.None);

        var payload = result.Payload.ShouldBeOfType<ImagePayload>();
        payload.Width.ShouldBe(100);
        payload.Height.ShouldBe(40);
        payload.Data.ToArray().ShouldBe(png);
    }

    [Fact]
    public async Task 数值小于等于0_返回InvalidInput()
    {
        var png = CreatePng(40, 40);

        var result = await new ResizeTool().ExecuteAsync(
            Context(png, new Dictionary<string, string> { ["value"] = "0" }),
            CancellationToken.None);

        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 超大目标_尺寸被夹到上限()
    {
        var png = CreatePng(100, 40);

        var result = await new ResizeTool().ExecuteAsync(
            Context(png, new Dictionary<string, string> { ["mode"] = "按边长", ["value"] = "999999" }),
            CancellationToken.None);

        var payload = result.Payload.ShouldBeOfType<ImagePayload>();
        payload.Width.ShouldBeLessThanOrEqualTo(10000);
        payload.Height.ShouldBeLessThanOrEqualTo(10000);
    }

    [Fact]
    public void 描述符_声明图片输入与图像分类()
    {
        var descriptor = new ResizeTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.images.resize");
        descriptor.Accepts.ShouldBe(InputShape.Image);
        descriptor.Produces.ShouldBe(OutputShape.Image);
        descriptor.Category.ShouldBe("images");
        descriptor.Keywords.Length.ShouldBeGreaterThanOrEqualTo(3);
    }
}
