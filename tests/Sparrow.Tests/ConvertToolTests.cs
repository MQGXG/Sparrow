using Shouldly;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Images.Convert;
using Xunit;

namespace Sparrow.Tests;

public sealed class ConvertToolTests
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
        canvas.Clear(SkiaSharp.SKColors.CornflowerBlue);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    [Fact]
    public async Task 非图片输入_返回InvalidInput()
    {
        var result = await new ConvertTool().ExecuteAsync(
            new ToolContext { Input = new TextInput("not image") },
            CancellationToken.None);

        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 空图片_返回InvalidInput()
    {
        var result = await new ConvertTool().ExecuteAsync(
            new ToolContext { Input = new ImageInput(ReadOnlyMemory<byte>.Empty, ImageFormat.Png) },
            CancellationToken.None);

        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 转JPEG_输出JPEG字节且尺寸不变()
    {
        var png = CreatePng(100, 40);

        var result = await new ConvertTool().ExecuteAsync(
            Context(png, new Dictionary<string, string> { ["format"] = "JPEG", ["quality"] = "80" }),
            CancellationToken.None);

        var payload = result.Payload.ShouldBeOfType<ImagePayload>();
        payload.Format.ShouldBe(ImageFormat.Jpeg);
        payload.Width.ShouldBe(100);
        payload.Height.ShouldBe(40);

        // JPEG 魔数 FF D8
        var bytes = payload.Data.ToArray();
        ((int)bytes[0]).ShouldBe(0xFF);
        ((int)bytes[1]).ShouldBe(0xD8);
        using var decoded = SkiaSharp.SKBitmap.Decode(bytes);
        decoded.ShouldNotBeNull();
    }

    [Fact]
    public async Task 转WebP_输出可解码()
    {
        var png = CreatePng(60, 30);

        var result = await new ConvertTool().ExecuteAsync(
            Context(png, new Dictionary<string, string> { ["format"] = "WebP" }),
            CancellationToken.None);

        var payload = result.Payload.ShouldBeOfType<ImagePayload>();
        payload.Format.ShouldBe(ImageFormat.Webp);

        var bytes = payload.Data.ToArray();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4).ShouldBe("WEBP");
    }

    [Fact]
    public async Task 质量超出范围_被夹取而不是报错()
    {
        var png = CreatePng(40, 40);

        var result = await new ConvertTool().ExecuteAsync(
            Context(png, new Dictionary<string, string> { ["format"] = "JPEG", ["quality"] = "500" }),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task 不是图片的字节_返回InvalidInput()
    {
        var result = await new ConvertTool().ExecuteAsync(
            Context([1, 2, 3, 4, 5]),
            CancellationToken.None);

        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
        result.Error.Message.ShouldContain("解码");
    }

    [Fact]
    public void 描述符_声明图片输入与图像分类()
    {
        var descriptor = new ConvertTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.images.convert");
        descriptor.Accepts.ShouldBe(InputShape.Image);
        descriptor.Produces.ShouldBe(OutputShape.Image);
        descriptor.Category.ShouldBe("images");
        descriptor.Keywords.Length.ShouldBeGreaterThanOrEqualTo(3);
    }
}
