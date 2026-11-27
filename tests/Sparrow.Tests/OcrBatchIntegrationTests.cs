using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 多张识别的真实子进程路径：一次会话连续识别多张。
///
/// 这条测试保护的是 worker 协议本身——多帧 stdin、每帧一行 stdout、
/// 模型只初始化一次。假 runner 测不到这些。
/// </summary>
public sealed class OcrBatchIntegrationTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public async Task 真实worker_一次会话识别多张()
    {
        // 【修改】按当前测试程序集的构建配置找插件，避免 CI 的 Release 构建去 Debug 目录找。
        var pluginDirectory = TestRepoPaths.PluginOutput("sparrow.plugin.ocr", "net10.0");

        Directory.Exists(pluginDirectory).ShouldBeTrue(
            $"OCR 插件没有构建：{pluginDirectory}");

        var temp = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var first = Path.Combine(temp, "one.png");
            var second = Path.Combine(temp, "two.png");
            File.WriteAllBytes(first, CreatePng("ONE"));
            File.WriteAllBytes(second, CreatePng("TWO"));

            var host = new ToolHost("sparrow.plugin.ocr", pluginDirectory, new NullLog());

            var result = await new OcrImageTool().ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput(
                    [
                        new ImageInput(File.ReadAllBytes(first), ImageFormat.Png) { Name = "one.png" },
                        new ImageInput(File.ReadAllBytes(second), ImageFormat.Png) { Name = "two.png" }
                    ]),
                    Host = host
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue(result.Error?.Message);

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
            text.ShouldContain("1. one.png");
            text.ShouldContain("2. two.png");
            text.ToUpperInvariant().ShouldContain("ONE");
            text.ToUpperInvariant().ShouldContain("TWO");
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    private static byte[] CreatePng(string word)
    {
        using var bitmap = new SkiaSharp.SKBitmap(400, 100);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.White);

        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 36);
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black };
        canvas.DrawText(word, 20, 70, font, paint);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
