using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>本地模型清单与缓存路径；真正下载需要网络，这里只测纯逻辑。</summary>
public sealed class LocalModelManagerTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public void 清单包含三个可选模型()
    {
        var manager = new LocalModelManager(new NullLog(), Path.GetTempPath());

        manager.All.Count.ShouldBe(3);
        manager.All.Select(model => model.Id).ShouldBe(
            ["hy-mt-1.8b", "hy-mt-7b", "translategemma-4b"]);

        // 每个模型都要有内存要求和许可说明，设置页直接展示
        manager.All.ShouldAllBe(model => model.MemoryGb > 0);
        manager.All.ShouldAllBe(model => model.License.Length > 0);
    }

    [Fact]
    public void 未下载时IsInstalled为假且路径指向模型目录()
    {
        var root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        var manager = new LocalModelManager(new NullLog(), root);

        manager.IsInstalled("hy-mt-1.8b").ShouldBeFalse();
        manager.PathFor("hy-mt-1.8b").ShouldBe(
            Path.Combine(root, "hy-mt-1.8b", "model.gguf"));
    }

    [Fact]
    public async Task 没有下载源的模型直接返回空()
    {
        var root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        var manager = new LocalModelManager(new NullLog(), root);

        // TranslateGemma 目前没配下载源，不能让调用方卡在下载里
        var path = await manager.EnsureAsync(
            "translategemma-4b",
            cancellationToken: TestContext.Current.CancellationToken);

        path.ShouldBeNull();
    }

    [Fact]
    public async Task 未知模型返回空而不是抛异常()
    {
        var manager = new LocalModelManager(new NullLog(), Path.GetTempPath());

        (await manager.EnsureAsync(
            "no-such-model",
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeNull();
        (await manager.DeleteAsync(
            "no-such-model",
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task 删除未安装的模型返回假()
    {
        var root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        var manager = new LocalModelManager(new NullLog(), root);

        (await manager.DeleteAsync(
            "hy-mt-1.8b",
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}
