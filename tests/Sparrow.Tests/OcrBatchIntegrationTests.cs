using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 批量识别的真实子进程路径：一次会话连续识别多张。
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
        var repoRoot = FindRepoRoot();
        var pluginDirectory = Path.Combine(
            repoRoot, "plugins", "sparrow.plugin.ocr", "bin", "Debug", "net10.0");

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

            var result = await new OcrBatchTool().ExecuteAsync(
                new ToolContext
                {
                    Input = new FileSetInput([first, second]),
                    Host = host,
                    Files = new ScopedFileAccess([first, second])
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue(result.Error?.Message);

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
            text.ShouldContain("## one.png");
            text.ShouldContain("## two.png");
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
