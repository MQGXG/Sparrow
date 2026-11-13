using Shouldly;
using Sparrow.App;
using Sparrow.App.History;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 工具墙 / 工具台的 ViewModel 行为。
///
/// 用临时插件目录 + 测试输出目录里的真实插件 dll 建两个分类，
/// 覆盖两个曾经踩过的坑：分类选中态不同步、启动时「最近使用」读不到历史。
/// </summary>
public sealed class WorkspaceViewModelTests
{
    private const string JsonManifest = """
        {
          "id": "sparrow.plugin.json-format",
          "name": "JSON 格式化",
          "version": "1.0.0",
          "sdkVersion": "1.0",
          "entryAssembly": "Sparrow.Plugin.Json.Format.dll",
          "tools": ["sparrow.json.format"]
        }
        """;

    private const string Base64Manifest = """
        {
          "id": "sparrow.plugin.dev-base64",
          "name": "Base64 编解码",
          "version": "1.0.0",
          "sdkVersion": "1.0",
          "entryAssembly": "Sparrow.Plugin.Dev.Base64.dll",
          "tools": ["sparrow.dev.base64"]
        }
        """;

    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }
        public byte[]? ImagePng { get; set; }
        public string? GetText() => Text;
        public byte[]? GetImagePng() => ImagePng;
        public bool SetText(string text)
        {
            Text = text;
            return true;
        }
    }

    private sealed class FakeHistory : IHistoryStore
    {
        public List<HistoryEntry> Entries { get; } = [];

        public void Add(HistoryEntry entry) => Entries.Insert(0, entry);

        public IReadOnlyList<HistoryEntry> Recent(int count) => Entries.Take(count).ToList();

        public void Clear() => Entries.Clear();
    }

    /// <summary>用完即弃的插件根目录，放 json + base64 两个插件凑出两个分类。</summary>
    private sealed class TempPluginRoot : IDisposable
    {
        public string Path { get; }

        public TempPluginRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);

            CreatePlugin("sparrow.plugin.json-format", JsonManifest, "Sparrow.Plugin.Json.Format.dll");
            CreatePlugin("sparrow.plugin.dev-base64", Base64Manifest, "Sparrow.Plugin.Dev.Base64.dll");
        }

        private void CreatePlugin(string directoryName, string manifestJson, string entryAssembly)
        {
            var directory = System.IO.Path.Combine(Path, directoryName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(System.IO.Path.Combine(directory, "plugin.json"), manifestJson);

            var name = System.IO.Path.GetFileNameWithoutExtension(entryAssembly);
            foreach (var file in new[] { entryAssembly, name + ".deps.json" })
            {
                var source = System.IO.Path.Combine(AppContext.BaseDirectory, file);
                if (File.Exists(source))
                    File.Copy(source, System.IO.Path.Combine(directory, file));
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // ALC 可能还持有 dll 句柄；临时目录留给系统清理
            }
        }
    }

    private static WorkspaceViewModel Create(
        ToolCatalog catalog,
        IHistoryStore history,
        ToolParameterStore? parameters = null)
    {
        var log = new NullLog();
        var settings = new AppSettings();
        var store = new SettingsStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N") + ".json"),
            log);

        var settingsViewModel = new SettingsViewModel(catalog, store, new StartupService(log), settings);

        return new WorkspaceViewModel(
            catalog,
            new FakeClipboard { Text = "clipboard" },
            log,
            history,
            settingsViewModel,
            parameters);
    }

    [Fact]
    public async Task 选择分类_书签条同步更新_列表只剩该分类()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();

        viewModel.Categories.First().Name.ShouldBe("全部");
        viewModel.Categories.First().IsSelected.ShouldBeTrue();

        viewModel.SelectCategory("json");

        viewModel.Categories.Single(category => category.Name == "json").IsSelected.ShouldBeTrue();
        viewModel.Categories.Single(category => category.Name == "全部").IsSelected.ShouldBeFalse();

        viewModel.Groups.ShouldHaveSingleItem().Category.ShouldBe("json");
        viewModel.Groups[0].Tools.ShouldHaveSingleItem().Name.ShouldBe("JSON 格式化");
    }

    [Fact]
    public async Task 启动时_最近使用从磁盘历史恢复()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());

        // 先构建注册表，拿到真实的工具名（历史按 Descriptor.Name 匹配）
        var json = catalog.GetRegistry().Search("json").ShouldHaveSingleItem();
        var history = new FakeHistory();
        history.Entries.Add(new HistoryEntry(
            "h1",
            json.Descriptor.Id,
            json.Descriptor.Name,
            "{ \"a\": 1 }",
            "",
            DateTimeOffset.Now,
            12));

        var viewModel = Create(catalog, history);

        await viewModel.ReloadAsync();

        viewModel.HasRecentTools.ShouldBeTrue();
        viewModel.RecentTools.ShouldHaveSingleItem().Tool.Name.ShouldBe(json.Descriptor.Name);
    }

    [Fact]
    public async Task 打开工具时_恢复上次保存的参数()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());

        var parameterPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sparrow-tests",
            Guid.NewGuid().ToString("N"),
            "parameters.json");
        var parameters = new ToolParameterStore(parameterPath, new NullLog());
        parameters.Save("sparrow.json.format", new Dictionary<string, string>
        {
            ["mode"] = "压缩",
            ["indent"] = "4"
        });

        var viewModel = Create(catalog, new FakeHistory(), parameters);

        await viewModel.ReloadAsync();

        var json = viewModel.Groups
            .SelectMany(group => group.Tools)
            .First(tool => tool.Entry.Descriptor.Id == "sparrow.json.format");

        viewModel.OpenTool(json);

        viewModel.Parameters.Single(parameter => parameter.Key == "mode").Value.ShouldBe("压缩");
        viewModel.Parameters.Single(parameter => parameter.Key == "indent").Value.ShouldBe("4");
    }

    [Fact]
    public async Task 拖入文本文件_直接填入输入框()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());
        await viewModel.ReloadAsync();

        var path = System.IO.Path.Combine(root.Path, "note.md");
        File.WriteAllText(path, "# 标题\n正文");

        viewModel.AcceptDroppedFiles([path]);

        viewModel.InputText.ShouldBe("# 标题\n正文");
    }

    [Fact]
    public async Task 拖入图片_记录为输入并可清除()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());
        await viewModel.ReloadAsync();

        var path = System.IO.Path.Combine(root.Path, "shot.png");
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

        viewModel.AcceptDroppedFiles([path]);

        viewModel.HasDroppedImage.ShouldBeTrue();

        viewModel.ClearDroppedImage();

        viewModel.HasDroppedImage.ShouldBeFalse();
    }

    [Fact]
    public async Task 打开最近页_隐藏工具墙并取消分类选中()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();
        viewModel.ShowRecent();

        viewModel.IsRecentOpen.ShouldBeTrue();
        viewModel.IsRecentVisible.ShouldBeTrue();
        viewModel.IsWallVisible.ShouldBeFalse();
        viewModel.IsToolsSelected.ShouldBeFalse();
        viewModel.Categories.ShouldAllBe(category => !category.IsSelected);
    }

    [Fact]
    public async Task 从最近页打开工具_返回时回到最近页()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();
        var tool = viewModel.VisibleTools.First();

        viewModel.ShowRecent();
        viewModel.OpenTool(tool, fromRecent: true);

        viewModel.IsDetailOpen.ShouldBeTrue();
        viewModel.IsRecentOpen.ShouldBeFalse();

        viewModel.CloseDetail();

        viewModel.IsDetailOpen.ShouldBeFalse();
        viewModel.IsRecentOpen.ShouldBeTrue();
        viewModel.IsRecentVisible.ShouldBeTrue();
    }

    [Fact]
    public async Task 点分类_退出最近页回到工具墙()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();
        viewModel.ShowRecent();
        viewModel.SelectCategory("json");

        viewModel.IsRecentOpen.ShouldBeFalse();
        viewModel.IsWallVisible.ShouldBeTrue();
        viewModel.Categories.Single(category => category.Name == "json").IsSelected.ShouldBeTrue();
    }

    [Fact]
    public async Task 工具墙与分组_共用同一批实例()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();

        var wall = viewModel.VisibleTools.First();
        var grouped = viewModel.Groups.SelectMany(group => group.Tools).First();

        ReferenceEquals(wall, grouped).ShouldBeTrue();
    }
}
