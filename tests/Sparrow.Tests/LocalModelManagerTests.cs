using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
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
    public void TranslateGemma_配置了国内优先的下载源()
    {
        var manager = new LocalModelManager(new NullLog(), Path.GetTempPath());
        var model = manager.All.Single(item => item.Id == "translategemma-4b");

        model.DownloadUrls.ShouldNotBeEmpty();
        model.DownloadUrls[0].ShouldStartWith("https://modelscope.cn/");
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

    [Fact]
    public void 有下载源的模型都优先走国内镜像()
    {
        var manager = new LocalModelManager(new NullLog(), Path.GetTempPath());

        var downloadable = manager.All.Where(model => model.DownloadUrls.Count > 0).ToList();
        downloadable.ShouldNotBeEmpty();

        // HuggingFace 在部分网络下连不通，第一个源必须是 ModelScope
        downloadable.ShouldAllBe(model =>
            model.DownloadUrls[0].StartsWith("https://modelscope.cn/", StringComparison.Ordinal));
    }

    [Fact]
    public void 自托管镜像_只换前缀且排在内置源前面()
    {
        var manager = new LocalModelManager(new NullLog(), Path.GetTempPath());

        manager.MirrorUrl = "https://models.example.com/sparrow/";
        var sources = manager.SourcesFor("hy-mt-1.8b");

        sources[0].ShouldBe(
            "https://models.example.com/sparrow/HY-MT1.5-1.8B-Q4_K_M.gguf");
        // 镜像失败后仍要能回落到内置源
        sources.Count.ShouldBe(manager.All.Single(m => m.Id == "hy-mt-1.8b").DownloadUrls.Count + 1);
        sources[^1].ShouldStartWith("https://huggingface.co/", Case.Sensitive);
    }

    [Fact]
    public void 没有镜像时直接用内置源()
    {
        var manager = new LocalModelManager(new NullLog(), Path.GetTempPath());

        manager.SourcesFor("hy-mt-1.8b")
            .ShouldBe(manager.All.Single(m => m.Id == "hy-mt-1.8b").DownloadUrls);

        // 未知模型给空列表，不是抛异常
        manager.SourcesFor("no-such-model").ShouldBeEmpty();

        // TranslateGemma 现在也有内置源（ModelScope 在前）
        manager.SourcesFor("translategemma-4b")
            .ShouldBe(manager.All.Single(m => m.Id == "translategemma-4b").DownloadUrls);
    }

    [Fact]
    public void 设置页列出离线模型并按磁盘状态区分按钮()
    {
        var root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var log = new NullLog();
        var manager = new LocalModelManager(log, root);

        // 造一个已下载的模型文件，状态必须来自文件系统而不是缓存
        var installed = manager.PathFor("hy-mt-1.8b");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllBytes(installed, [0]);

        var store = new SettingsStore(Path.Combine(root, "settings.json"), log);
        var catalog = new ToolCatalog(root, log, new AppSettings());
        var viewModel = new SettingsViewModel(
            catalog,
            store,
            new StartupService(log),
            new AppSettings(),
            localModels: manager);

        viewModel.HasLocalModels.ShouldBeTrue();
        viewModel.LocalModels.Count.ShouldBe(3);

        var downloaded = viewModel.LocalModels.Single(item => item.Info.Id == "hy-mt-1.8b");
        downloaded.IsInstalled.ShouldBeTrue();
        downloaded.ShowDelete.ShouldBeTrue();
        downloaded.ShowDownload.ShouldBeFalse();

        var notDownloaded = viewModel.LocalModels.Single(item => item.Info.Id == "hy-mt-7b");
        notDownloaded.IsInstalled.ShouldBeFalse();
        notDownloaded.ShowDownload.ShouldBeTrue();

        // TranslateGemma 已有下载源：和其他模型一样显示下载按钮
        var gemma = viewModel.LocalModels.Single(item => item.Info.Id == "translategemma-4b");
        gemma.ShowDownload.ShouldBeTrue();
        gemma.StateText.ShouldBe("未下载");
    }

    [Fact]
    public void 宿主没注入本地模型时设置页列表为空()
    {
        var root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var log = new NullLog();
        var store = new SettingsStore(Path.Combine(root, "settings.json"), log);
        var catalog = new ToolCatalog(root, log, new AppSettings());
        var viewModel = new SettingsViewModel(catalog, store, new StartupService(log), new AppSettings());

        viewModel.HasLocalModels.ShouldBeFalse();
        viewModel.LocalModels.ShouldBeEmpty();
    }
}
