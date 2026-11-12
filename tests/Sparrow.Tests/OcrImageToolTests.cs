using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

public sealed class OcrImageToolTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public async Task 非图片输入_返回InvalidInput()
    {
        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext { Input = new TextInput("not an image"), Host = NullToolHost.Instance },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 空图片_返回InvalidInput()
    {
        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext
            {
                Input = new ImageInput(ReadOnlyMemory<byte>.Empty, ImageFormat.Png),
                Host = NullToolHost.Instance
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 没有宿主_返回Failed而不是抛异常()
    {
        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext { Input = new ImageInput(new byte[] { 1, 2, 3 }, ImageFormat.Png) },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
    }

    [Fact]
    public async Task worker不存在_返回Failed且带上路径()
    {
        var host = new ToolHost("sparrow.plugin.ocr", Path.GetTempPath(), new NullLog());

        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext
            {
                Input = new ImageInput(new byte[] { 1, 2, 3 }, ImageFormat.Png),
                Host = host
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("worker");
    }

    [Fact]
    public void 描述符_声明图片输入与Ocr能力()
    {
        var descriptor = new OcrImageTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.ocr.image");
        descriptor.Accepts.ShouldBe(InputShape.Image);
        descriptor.Produces.ShouldBe(OutputShape.Text);
        descriptor.Requires.ShouldBe(PlatformCapability.Ocr);
        descriptor.Parameters.ShouldContain(p => p.Key == "language");
    }

    /// <summary>
    /// 真实 IPC：宿主 → 子进程 → 宿主。
    /// 依赖 OCR 插件构建目录里的 worker，路径从仓库根算，不依赖测试输出目录。
    /// </summary>
    [Fact]
    public async Task 假worker_能通过stdin收图并返回文本()
    {
        var repoRoot = FindRepoRoot();
        var pluginDirectory = Path.Combine(
            repoRoot, "plugins", "sparrow.plugin.ocr", "bin", "Debug", "net10.0");

        Directory.Exists(pluginDirectory).ShouldBeTrue(
            $"OCR 插件没有构建：{pluginDirectory}");

        var host = new ToolHost("sparrow.plugin.ocr", pluginDirectory, new NullLog());
        var png = CreatePng(400, 100);

        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext
            {
                Input = new ImageInput(png, ImageFormat.Png),
                Host = host
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<TextPayload>().Text.ShouldContain("HELL");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Sparrow.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("找不到仓库根目录（Sparrow.slnx）");
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);

        // 写一行文字，确保测的是「识别」而不是「处理空白图」
        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 36);
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
        canvas.DrawText("HELLO", 20, height - 30, font, paint);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
