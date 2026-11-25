// OcrImageToolTests 分部类：输入校验、worker 会话与翻译模式。
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
    public async Task 非图片输入_返回InvalidInput()
    {
        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext { Input = new TextInput("not an image"), Host = NullToolHost.Instance },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ToolErrorKind.InvalidInput);
    }

    [Fact]
    public async Task 空图片集_返回InvalidInput()
    {
        var result = await new OcrImageTool().ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([]),
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
            new ToolContext { Input = new ImageSetInput([Image("a.png")]) },
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
                Input = new ImageSetInput([Image("a.png")]),
                Host = host
            },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Error!.Message.ShouldContain("worker");
    }

    [Fact]
    public void 语言参数列出全部内置语言()
    {
        var language = new OcrImageTool().Descriptor.Parameters
            .Single(parameter => parameter.Key == "language");

        // 模型包本来就带 12 类，之前只暴露了 16 个代码，现在补齐
        language.Choices.Length.ShouldBe(24);
        language.Choices.ShouldContain("it");
        language.Choices.ShouldContain("te");

        language.ChoiceLabels.ShouldNotBeNull();
        language.ChoiceLabels!["it"].ShouldBe("意大利语（it）");
        language.ChoiceLabels["te"].ShouldBe("泰卢固语（te）");
    }

    [Fact]
    public void 描述符_声明图片集输入与Ocr能力()
    {
        var descriptor = new OcrImageTool().Descriptor;

        descriptor.Id.ShouldBe("sparrow.ocr.image");
        descriptor.Accepts.ShouldBe(InputShape.Images);
        descriptor.Produces.ShouldBe(OutputShape.Text);
        descriptor.Requires.ShouldBe(PlatformCapability.Ocr);
        descriptor.Parameters.ShouldContain(p => p.Key == "language");
        descriptor.Parameters.ShouldContain(p => p.Key == "layout");
        descriptor.Parameters.ShouldContain(p => p.Key == "reuse");
    }

    [Fact]
    public async Task 默认复用worker_连续识别只起一次会话()
    {
        var runner = new FakeRunner([Result("第一次"), Result("第二次")]);
        var tool = new OcrImageTool(runner, TimeSpan.FromMinutes(5));
        var host = CreateHost(CreateWorkerDirectory());

        for (var i = 0; i < 2; i++)
        {
            var result = await tool.ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = host
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
        }

        runner.Sessions.ShouldBe(1);
        runner.Session.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task 关闭复用后_每次识别都起新会话()
    {
        var runner = new FakeRunner([Result("第一次"), Result("第二次")]);
        var tool = new OcrImageTool(runner, TimeSpan.FromMinutes(5));
        var host = CreateHost(CreateWorkerDirectory());

        for (var i = 0; i < 2; i++)
        {
            var result = await tool.ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = host,
                    Parameters = new Dictionary<string, string>
                    {
                        [OcrParameters.ReuseKey] = OcrParameters.ReuseOff
                    }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
        }

        runner.Sessions.ShouldBe(2);
    }

    [Fact]
    public async Task 单张图片_输出纯文本不带文件名标题()
    {
        var runner = new FakeRunner([Result("HELLO")]);

        var result = await new OcrImageTool(runner).ExecuteAsync(
            new ToolContext
            {
                Input = new ImageSetInput([Image("a.png")]),
                Host = CreateHost(CreateWorkerDirectory())
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue();

        var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
        text.ShouldBe("HELLO");
        text.ShouldNotContain("##");
    }

    [Fact]
    public async Task 没配在线模型_用本地引擎翻译()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner([Result("hello world")]);
            var translation = new FakeTranslation("你好，世界");

            var host = new ToolHost(
                "sparrow.plugin.ocr",
                root,
                new NullLog(),
                localModels: new FakeModels("hy-mt-1.8b"),
                localTranslation: translation);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = host,
                    Parameters = new Dictionary<string, string> { ["translate"] = "中文" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
            text.ShouldContain("hello world");
            text.ShouldContain("译文");
            text.ShouldContain("你好，世界");

            translation.LastRequest.ShouldNotBeNull().TargetLanguage.ShouldBe("中文");
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
    public async Task 表格模式_走视觉模型并输出HTML()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var ai = new FakeAi();
            var result = await new OcrImageTool(new FakeRunner([Result("ignored")])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("table.png")]),
                    Host = CreateHost(root, ai),
                    Parameters = new Dictionary<string, string> { ["output"] = "table-html" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldContain("<table>");

            var request = ai.LastRequest.ShouldNotBeNull();
            request.Image.Length.ShouldBeGreaterThan(0);
            request.System.ShouldNotBeNull().ShouldContain("HTML");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 表格模式_去掉模型加的代码围栏()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var ai = new FakeAi { Reply = "```html\n<table><tr><td>1</td></tr></table>\n```" };

            var result = await new OcrImageTool(new FakeRunner([Result("ignored")])).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("table.png")]),
                    Host = CreateHost(root, ai),
                    Parameters = new Dictionary<string, string> { ["output"] = "table-md" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.Payload.ShouldBeOfType<OcrResultPayload>().Text
                .ShouldBe("| 1 |\n| --- |");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 表格模式_在线失败回落本地表格模型()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var ai = new FakeAi { Throw = new AiException(AiErrorKind.ProviderError, "网关超时") };
            var runner = new FakeRunner(["""{"html":"<table><tr><td>本地</td></tr></table>"}"""]);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("table.png")]),
                    Host = CreateHost(root, ai),
                    Parameters = new Dictionary<string, string> { ["output"] = "table-html" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldContain("本地");
            runner.LastMode.ShouldBe("table");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 表格模式_本地HTML转Markdown()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner(["""{"html":"<table><tr><th>A</th></tr><tr><td>1</td></tr></table>"}"""]);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("table.png")]),
                    Host = CreateHost(root),
                    Parameters = new Dictionary<string, string> { ["output"] = "table-md" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue();
            result.Payload.ShouldBeOfType<OcrResultPayload>().Text.ShouldBe("| A |\n| --- |\n| 1 |");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 表格模式_本地没有识别出表格时给出失败()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner(["""{"error":"没有识别出表格"}"""]);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("table.png")]),
                    Host = CreateHost(root),
                    Parameters = new Dictionary<string, string> { ["output"] = "table-html" }
                },
                CancellationToken.None);

            result.Success.ShouldBeFalse();
            result.Error!.Kind.ShouldBe(ToolErrorKind.Execution);
            result.Error.Message.ShouldContain("没有识别出表格");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
    [Fact]
    public async Task 选了翻译但模型不可用_保留原文并说明未翻译()
    {
        var root = CreateWorkerDirectory();
        try
        {
            var runner = new FakeRunner([Result("第一张的内容")]);

            var result = await new OcrImageTool(runner).ExecuteAsync(
                new ToolContext
                {
                    Input = new ImageSetInput([Image("a.png")]),
                    Host = CreateHost(root),
                    // 参数键与 OcrParameters.TranslateKey 一致（那个常量是 internal）
                    Parameters = new Dictionary<string, string> { ["translate"] = "English" }
                },
                CancellationToken.None);

            result.Success.ShouldBeTrue("翻译是附加能力，没配模型不能拖垮识别");

            var text = result.Payload.ShouldBeOfType<OcrResultPayload>().Text;
            text.ShouldContain("第一张的内容");

            // 不能凭空造译文，但必须告诉用户「没翻」以及去哪里配
            text.ShouldContain("未翻译");
            text.ShouldContain("设置");
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
}
