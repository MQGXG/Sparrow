// OcrImageToolTests 分部类：批量图片、失败处理、模型选择与格式转换。
using System.Runtime.CompilerServices;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;
using Xunit;

namespace Sparrow.Tests;

public sealed partial class OcrImageToolTests
{
    [Fact]
    public async Task 多图标题带序号和尺寸()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner([Result("甲"), Result("乙")]);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput(
                    [
                        new ImageInput(new byte[] { 1, 2, 3 }, ImageFormat.Png)
                        {
                            Name = "a.png", Width = 1200, Height = 800
                        },
                        new ImageInput(new byte[] { 4, 5, 6 }, ImageFormat.Png)
                        {
                            Name = "b.png", Width = 640, Height = 480
                        }
                    ]),
                    Host = CreateHost(root)
                },
                CancellationToken.None);

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;

            // 标题要能让用户对上是哪张图
            text.ShouldContain("1. a.png（1200×800）");
            text.ShouldContain("2. b.png（640×480）");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    [Fact]
    public async Task 多张图片_复用同一会话并按文件名分节()
    {
        var runner = new FakeRunner([Result("第一张"), Result("第二张")]);
        var progress = new CollectingProgress();

        var result = await new OcrImageTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png"), Image("b.jpg")]),
                Host = CreateHost(CreateWorkerDirectory()),
                Progress = progress
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
        text.ShouldContain("1. a.png");
        text.ShouldContain("第一张");
        text.ShouldContain("2. b.jpg");
        text.ShouldContain("第二张");

        // 模型只加载一次
        runner.Sessions.ShouldBe(1);
        runner.Session.Calls.ShouldBe(2);
        progress.Reports.ShouldNotBeEmpty();
        progress.Reports.Last().Percent.ShouldBe(1);
    }

    [Fact]
    public async Task 单张失败_不影响其他图片()
    {
        var runner = new FakeRunner(["""{"error":"无法解码图片"}""", Result("第二张")]);

        var result = await new OcrImageTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png"), Image("b.png")]),
                Host = CreateHost(CreateWorkerDirectory())
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
        text.ShouldContain("第二张");
        text.ShouldContain("未识别：1 个文件");
        text.ShouldContain("- 1. a.png");
    }

    [Fact]
    public async Task 全部失败_返回Failed()
    {
        var runner = new FakeRunner(["""{"error":"坏图"}"""]);

        var result = await new OcrImageTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png")]),
                Host = CreateHost(CreateWorkerDirectory())
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("没有识别到文字");
    }

    [Fact]
    public async Task 引擎选在线_即使有本地模型也只走在线()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var ai = new FakeAi { Reply = "1|你好，世界" };
            var translation = new FakeTranslation("1|本地译文");

            var host = new ToolHost(
                "sparrow.plugin.ocr",
                root,
                new NullLog(),
                ai: ai,
                localModels: new FakeModels("hy-mt-1.8b"),
                localTranslation: translation);

            var result = await new OcrImageTool(new FakeRunner([Result("hello world")])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = host,
                    Parameters = new Dictionary<string, string>
                    {
                        ["translate"] = "中文",
                        ["engine"] = "ai"
                    }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldContain("你好，世界");

            ai.LastRequest.ShouldNotBeNull();
            translation.LastRequest.ShouldBeNull();   // 没走本地
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 引擎选指定本地模型_即使在线可用也只走本地()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var ai = new FakeAi { Reply = "1|在线译文" };
            var translation = new FakeTranslation("1|本地译文");

            var host = new ToolHost(
                "sparrow.plugin.ocr",
                root,
                new NullLog(),
                ai: ai,
                localModels: new FakeModels("hy-mt-1.8b"),
                localTranslation: translation);

            var result = await new OcrImageTool(new FakeRunner([Result("hello world")])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = host,
                    Parameters = new Dictionary<string, string>
                    {
                        ["translate"] = "中文",
                        ["engine"] = "local:hy-mt-1.8b"
                    }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldContain("本地译文");

            translation.LastRequest.ShouldNotBeNull().ModelId.ShouldBe("hy-mt-1.8b");
            ai.LastRequest.ShouldBeNull();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 引擎选在线但没配_不悄悄换本地而是提示未翻译()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var ai = new FakeAi { IsAvailable = false };
            var translation = new FakeTranslation("1|本地译文");

            var host = new ToolHost(
                "sparrow.plugin.ocr",
                root,
                new NullLog(),
                ai: ai,
                localModels: new FakeModels("hy-mt-1.8b"),
                localTranslation: translation);

            var result = await new OcrImageTool(new FakeRunner([Result("hello world")])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = host,
                    Parameters = new Dictionary<string, string>
                    {
                        ["translate"] = "中文",
                        ["engine"] = "ai"
                    }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
            text.ShouldContain("hello world");
            text.ShouldContain("未翻译");
            translation.LastRequest.ShouldBeNull();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 引擎取值_允许指定本地模型_未知值回退自动()
    {
        OcrParameters.ResolveEngine(Context("engine", "local:hy-mt-7b")).ShouldBe("local:hy-mt-7b");
        OcrParameters.ResolveEngine(Context("engine", "未知")).ShouldBe("auto");
        OcrParameters.ResolveEngine(Context()).ShouldBe("auto");
    }

    private static ToolContext Context(params string[] pairs)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index + 1 < pairs.Length; index += 2)
            parameters[pairs[index]] = pairs[index + 1];

        return new ToolContext
        {
            Input = new TextInput("test"),
            Parameters = parameters
        };
    }

    [Fact]
    public void 多图拼接_关闭失败项只留正文()
    {
        var text = OcrImageTool.ComposeBatchText(
        [
            new TextSection { Title = "a.png", Text = "一" },
            new TextSection { Title = "b.png", Status = TextSectionStatus.Failed },
            new TextSection { Title = "c.png", Text = "三" }
        ]);

        text.ShouldContain("## a.png");
        text.ShouldContain("## c.png");
        text.ShouldContain("未识别：1 个文件");
        text.ShouldContain("- b.png");
    }

    [Fact]
    public async Task 流式翻译_边翻边上报部分行框()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var json = """
            {"text":"订单号 张三","regions":[
              {"text":"订单号","x":0,"y":0,"width":80,"height":20,"score":0.98},
              {"text":"张三","x":0,"y":40,"width":40,"height":20,"score":0.98}
            ]}
            """;

            var ai = new FakeAi();
            ai.StreamSnapshots.Add("1|Order No.\n2|");
            ai.StreamSnapshots.Add("1|Order No.\n2|Zhang San");

            var progress = new CollectingProgress();

            var result = await new OcrImageTool(new FakeRunner([json])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = CreateHost(root, ai),
                    Progress = progress,
                    Parameters = new Dictionary<string, string>
                    {
                        ["translate"] = "English",
                        ["engine"] = "ai"
                    }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();

            // 第一帧：行框先到，还没有译文
            progress.Reports.Any(report =>
                    report.Lines is { Count: 2 } && report.Lines.All(line => line.Translation is null))
                .ShouldBeTrue();

            // 中间帧：第一行已经译完就开始画
            progress.Reports.Any(report =>
                    report.Lines is { Count: 2 } && report.Lines[0].Translation == "Order No.")
                .ShouldBeTrue();

            var payload = result.Payload.ShouldBeOfType<OcrResultPayload>();
            payload.Lines[0].Translation.ShouldBe("Order No.");
            payload.Lines[1].Translation.ShouldBe("Zhang San");
            payload.Text.ShouldContain("—— 译文（English）——");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 翻译请求_每行带编号()
    {
        OcrImageTool.BuildTranslationRequest(["订单号", "ORD-001"])
            .ShouldBe("1|订单号\n2|ORD-001");
    }

    [Fact]
    public void 译文解析_按编号回填且允许缺号()
    {
        var parsed = OcrImageTool.ParseTranslationResponse(
            "1|Order No.\n3|Amount", 4);

        parsed.ShouldNotBeNull();
        parsed![0].ShouldBe("Order No.");
        parsed[1].ShouldBeNull();
        parsed[2].ShouldBe("Amount");
        parsed[3].ShouldBeNull();
    }

    [Fact]
    public void 译文解析_忽略编号时按位置回填()
    {
        var parsed = OcrImageTool.ParseTranslationResponse("Order No.\nCustomer", 2);

        parsed.ShouldNotBeNull();
        parsed![0].ShouldBe("Order No.");
        parsed[1].ShouldBe("Customer");
    }

    [Fact]
    public void 译文解析_行数对不上且无编号时返回空()
    {
        OcrImageTool.ParseTranslationResponse("只有一行", 3).ShouldBeNull();
    }

    [Fact]
    public void 译文回填_缺号的行保持原文()
    {
        var lines = new List<OcrTextLine>
        {
            new("订单号", 0, 0, 80, 20),
            new("ORD-001", 0, 40, 80, 20)
        };

        var result = OcrImageTool.AttachTranslations(lines, ["Order No.", null]);

        result.Count.ShouldBe(2);
        result[0].Translation.ShouldBe("Order No.");

        // 第二行没译文：保持原样，不画任何东西
        result[1].Translation.ShouldBeNull();
    }

    [Fact]
    public void 译文回填_原样返回的格子不重画()
    {
        var lines = new List<OcrTextLine>
        {
            new("ORD-001", 0, 0, 80, 20),
            new("张三", 0, 40, 80, 20)
        };

        var result = OcrImageTool.AttachTranslations(lines, ["ORD-001", "Zhang San"]);

        result[0].Translation.ShouldBeNull();     // 编号原样返回
        result[1].Translation.ShouldBe("Zhang San");
    }

    /// <summary>
    /// 真实 IPC：宿主 → 子进程 → 宿主。
    /// 依赖 OCR 插件构建目录里的 worker，路径从仓库根算，不依赖测试输出目录。
    /// </summary>
    [Fact]
    public async Task 真实worker_单张图片能识别()
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
                Input = new ImageSetInput(
                [
                    new ImageInput(png, ImageFormat.Png) { Name = "hello.png" }
                ]),
                Host = host
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldContain("HELL");
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
