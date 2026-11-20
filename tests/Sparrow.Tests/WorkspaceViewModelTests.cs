using Shouldly;
using Sparrow.App;
using Sparrow.App.History;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
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

    /// <summary>图片集工具的来源：OCR 接受 1..N 张图片。</summary>
    private const string OcrManifest = """
        {
          "id": "sparrow.plugin.ocr",
          "name": "OCR",
          "version": "0.3.0",
          "sdkVersion": "1.0",
          "entryAssembly": "Sparrow.Plugin.Ocr.dll",
          "tools": ["sparrow.ocr.image"]
        }
        """;

    /// <summary>文件类工具的来源：文件哈希接受 Files 输入。</summary>
    private const string FilesHashManifest = """
        {
          "id": "sparrow.plugin.files-hash",
          "name": "文件哈希",
          "version": "0.1.0",
          "sdkVersion": "1.0",
          "entryAssembly": "Sparrow.Plugin.Files.Hash.dll",
          "tools": ["sparrow.files.hash"]
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
        public bool SetImagePng(byte[] png)
        {
            ImagePng = png;
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
            CreatePlugin("sparrow.plugin.ocr", OcrManifest, "Sparrow.Plugin.Ocr.dll");
            CreatePlugin("sparrow.plugin.files-hash", FilesHashManifest, "Sparrow.Plugin.Files.Hash.dll");
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
        ToolParameterStore? parameters = null,
        IClipboardAccess? clipboard = null)
    {
        var log = new NullLog();
        var settings = new AppSettings();
        var store = new SettingsStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N") + ".json"),
            log);

        var settingsViewModel = new SettingsViewModel(catalog, store, new StartupService(log), settings);

        return new WorkspaceViewModel(
            catalog,
            clipboard ?? new FakeClipboard { Text = "clipboard" },
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

        var target = viewModel.Categories.First(category => category.Name != "全部");
        viewModel.SelectCategory(target.Name);

        viewModel.Categories.Single(category => category.Name == target.Name).IsSelected.ShouldBeTrue();
        viewModel.Categories.Single(category => category.Name == "全部").IsSelected.ShouldBeFalse();

        // 分类仍然是过滤：只剩该分类的工具与分组
        viewModel.Groups.ShouldHaveSingleItem().Category.ShouldBe(target.Name);
        viewModel.Groups[0].IsExpanded.ShouldBeTrue();
        viewModel.VisibleTools.Count.ShouldBe(1);
    }

    [Fact]
    public async Task 组标题点击_折叠状态可切换()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();

        var group = viewModel.Groups.First();
        group.IsExpanded.ShouldBeTrue();

        viewModel.ToggleGroup(group.Category);
        group.IsExpanded.ShouldBeFalse();

        viewModel.ToggleGroup(group.Category);
        group.IsExpanded.ShouldBeTrue();
    }

    [Fact]
    public async Task 搜索时_所有分组强制展开()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();

        var category = viewModel.Groups.First().Category;
        viewModel.ToggleGroup(category);
        viewModel.Groups.First(group => group.Category == category).IsExpanded.ShouldBeFalse();

        viewModel.ToolQuery = "json";
        viewModel.Groups.ShouldAllBe(group => group.IsExpanded);

        viewModel.ToolQuery = string.Empty;
        viewModel.Groups.Single(group => group.Category == category).IsExpanded.ShouldBeFalse();
    }

    [Fact]
    public async Task 最近页输入搜索词_回到工具墙()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();
        viewModel.ShowRecent();
        viewModel.ToolQuery = "json";

        viewModel.IsRecentOpen.ShouldBeFalse();
        viewModel.IsWallVisible.ShouldBeTrue();
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
    public async Task 拖入单张图片_进入图片集()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());
        await viewModel.ReloadAsync();

        var path = System.IO.Path.Combine(root.Path, "shot.png");
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

        viewModel.AcceptDroppedFiles([path]);

        // 合并后单张也走图片集：不必先打开「图片文字识别」再拖
        viewModel.IsImagesInput.ShouldBeTrue();
        viewModel.InputImageCount.ShouldBe(1);

        viewModel.ClearInputImages();

        viewModel.InputImageCount.ShouldBe(0);
    }

    [Fact]
    public async Task 轮播切换_边界与跳转()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());
        await viewModel.ReloadAsync();

        var paths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var path = System.IO.Path.Combine(root.Path, $"img{i}.png");
            File.WriteAllBytes(path, [1, 2, 3]);
            paths.Add(path);
        }

        viewModel.AcceptDroppedFiles(paths);

        viewModel.InputImageCount.ShouldBe(3);
        viewModel.CurrentImageIndex.ShouldBe(0);
        viewModel.CanGoPreviousImage.ShouldBeFalse("第一张没有上一张");
        viewModel.CanGoNextImage.ShouldBeTrue();
        viewModel.ImageCounter.ShouldBe("1 / 3");

        viewModel.NextImage();
        viewModel.CurrentImageIndex.ShouldBe(1);
        viewModel.ImageCounter.ShouldBe("2 / 3");

        viewModel.NextImage();
        viewModel.NextImage();
        viewModel.CurrentImageIndex.ShouldBe(2, "最后一张不能再往后");
        viewModel.CanGoNextImage.ShouldBeFalse();

        // 点小缩略图直接跳
        viewModel.SelectImage(viewModel.InputImages[0]);
        viewModel.CurrentImageIndex.ShouldBe(0);

        viewModel.PreviousImage();
        viewModel.CurrentImageIndex.ShouldBe(0, "第一张不能再往前");

        // 清空后索引要夹回 0
        viewModel.ClearInputImages();
        viewModel.CurrentImageIndex.ShouldBe(0);
        viewModel.ImageCounter.ShouldBeEmpty();
    }

    [Fact]
    public async Task 拖入多张图片_合并进图片集并切到OCRTool()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());
        await viewModel.ReloadAsync();

        var first = System.IO.Path.Combine(root.Path, "a.png");
        var second = System.IO.Path.Combine(root.Path, "b.png");
        File.WriteAllBytes(first, [1, 2, 3]);
        File.WriteAllBytes(second, [4, 5, 6]);

        viewModel.AcceptDroppedFiles([first, second]);

        // 全是图片：自动进入图片集工具，不再要求先选对卡片
        viewModel.IsDetailOpen.ShouldBeTrue();
        viewModel.IsImagesInput.ShouldBeTrue();
        viewModel.SelectedTool!.Entry.Descriptor.Id.ShouldBe("sparrow.ocr.image");

        viewModel.InputImageCount.ShouldBe(2);
        viewModel.InputHint.ShouldContain("2 张图片");
        viewModel.CanClearInputImages.ShouldBeTrue();

        viewModel.ClearInputImages();

        viewModel.InputImageCount.ShouldBe(0);
        viewModel.CanClearInputImages.ShouldBeFalse();
    }

    [Fact]
    public async Task 拖入多个非图片文件_切到文件类工具并保留文件清单()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());
        await viewModel.ReloadAsync();

        var first = System.IO.Path.Combine(root.Path, "a.txt");
        var second = System.IO.Path.Combine(root.Path, "b.pdf");
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");

        viewModel.AcceptDroppedFiles([first, second]);

        viewModel.IsDetailOpen.ShouldBeTrue();
        viewModel.IsFilesInput.ShouldBeTrue();
        viewModel.SelectedTool!.Entry.Descriptor.Id.ShouldBe("sparrow.files.hash");

        viewModel.DroppedFileCount.ShouldBe(2);
        viewModel.InputHint.ShouldContain("2 个文件");
        viewModel.CanClearDroppedFiles.ShouldBeTrue();

        viewModel.ClearDroppedFiles();

        viewModel.DroppedFileCount.ShouldBe(0);
        viewModel.CanClearDroppedFiles.ShouldBeFalse();
    }

    [Fact]
    public async Task 文件类工具没有文件_执行时给出明确提示而不是拿剪贴板文本()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());
        await viewModel.ReloadAsync();

        var tool = viewModel.Groups
            .SelectMany(group => group.Tools)
            .First(item => item.Entry.Descriptor.Id == "sparrow.files.hash");

        viewModel.OpenTool(tool);
        await viewModel.ExecuteAsync();

        viewModel.HasError.ShouldBeTrue();
        viewModel.ErrorText!.ShouldContain("选择文件");
    }

    [Fact]
    public async Task 图片集工具没有图片_执行时给出明确提示()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());
        await viewModel.ReloadAsync();

        var tool = viewModel.Groups
            .SelectMany(group => group.Tools)
            .First(item => item.Entry.Descriptor.Id == "sparrow.ocr.image");

        viewModel.OpenTool(tool);
        await viewModel.ExecuteAsync();

        viewModel.HasError.ShouldBeTrue();
        viewModel.ErrorText!.ShouldContain("添加图片");
    }

    [Fact]
    public async Task 剪贴板图片加入图片集_并能在预览里打开()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var clipboard = new FakeClipboard { ImagePng = [1, 2, 3] };
        var viewModel = Create(catalog, new FakeHistory(), clipboard: clipboard);
        await viewModel.ReloadAsync();

        viewModel.AddClipboardImage();

        viewModel.IsImagesInput.ShouldBeTrue();
        viewModel.InputImageCount.ShouldBe(1);
        viewModel.InputImages[0].Name.ShouldBe("剪贴板图片");

        viewModel.ShowImagePreview(viewModel.InputImages[0]);

        viewModel.IsImagePreviewVisible.ShouldBeTrue();
        viewModel.PreviewImagePng!.SequenceEqual(new byte[] { 1, 2, 3 }).ShouldBeTrue();

        viewModel.HideImagePreview();

        viewModel.IsImagePreviewVisible.ShouldBeFalse();
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

        var wall = viewModel.VisibleTools.ToList();
        var grouped = viewModel.Groups.SelectMany(group => group.Tools).ToList();

        // 分组只是把同一批实例换了个顺序展示，不能重新 new
        grouped.Count.ShouldBe(wall.Count);
        grouped.ShouldBe(wall, ignoreOrder: true);
    }

    [Fact]
    public async Task 设置分段_同一时间只选中一个()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();

        var settings = viewModel.Settings;
        settings.IsPluginsSelected.ShouldBeTrue();

        settings.SelectSection(SettingsSection.Model);

        settings.IsModelSelected.ShouldBeTrue();
        settings.IsPluginsSelected.ShouldBeFalse();
        settings.IsHotkeysSelected.ShouldBeFalse();
        settings.IsGeneralSelected.ShouldBeFalse();
        settings.IsAboutSelected.ShouldBeFalse();
    }

    private sealed class FakeSvgTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "test.svg",
            Name = "SVG 测试",
            Category = "test",
            Accepts = InputShape.Text,
            Produces = OutputShape.Svg
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok(new SvgPayload(
                """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" fill="#E5A44C"/></svg>""")
            {
                Width = 32,
                Height = 32
            }));
    }

    [Fact]
    public async Task SVG结果_生成预览并可切换源码()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();

        var entry = new ToolEntry { Tool = new FakeSvgTool(), Host = NullToolHost.Instance };
        viewModel.OpenTool(new ToolItemViewModel(entry));
        viewModel.InputText = "画一个方块";

        await viewModel.ExecuteAsync();

        viewModel.HasSvgResult.ShouldBeTrue();
        viewModel.IsSvgPreviewVisible.ShouldBeTrue();
        viewModel.SvgPreviewPng.ShouldNotBeNull();
        viewModel.ResultText.ShouldNotBeNull().ShouldContain("<svg");

        viewModel.ShowSvgSourceView();
        viewModel.IsSvgSourceVisible.ShouldBeTrue();
        viewModel.IsSvgPreviewVisible.ShouldBeFalse();

        viewModel.ShowSvgPreview();
        viewModel.IsSvgPreviewVisible.ShouldBeTrue();
    }

    [Fact]
    public async Task 表格结果_按文本展示而不是报失败()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();

        var entry = new ToolEntry { Tool = new FakeTableTool(), Host = NullToolHost.Instance };
        viewModel.OpenTool(new ToolItemViewModel(entry));
        viewModel.InputText = "x";

        await viewModel.ExecuteAsync();

        viewModel.HasResult.ShouldBeTrue();
        viewModel.HasError.ShouldBeFalse();
        viewModel.IsTextResultVisible.ShouldBeTrue();

        var text = viewModel.ResultText.ShouldNotBeNull();
        text.ShouldContain("列");
        text.ShouldContain("值");
    }

    /// <summary>
    /// 表格 / 文件列表之类的载荷以前会掉进「执行失败」——成功就是成功，
    /// 宿主不认识的新载荷只能显示占位说明，不能谎报失败。
    /// </summary>
    [Fact]
    public async Task 未知载荷类型_不报执行失败()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());
        var viewModel = Create(catalog, new FakeHistory());

        await viewModel.ReloadAsync();

        var entry = new ToolEntry { Tool = new FakeUnknownPayloadTool(), Host = NullToolHost.Instance };
        viewModel.OpenTool(new ToolItemViewModel(entry));
        viewModel.InputText = "x";

        await viewModel.ExecuteAsync();

        viewModel.HasResult.ShouldBeTrue();
        viewModel.HasError.ShouldBeFalse();
        viewModel.ResultText!.ShouldContain("暂不支持展示");
    }

    private sealed class FakeTableTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "test.table",
            Name = "表格测试",
            Category = "test",
            Accepts = InputShape.Text,
            Produces = OutputShape.Table
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok(new TablePayload(["列"], [["值"]])));
    }

    private sealed record UnknownPayload : IToolPayload;

    private sealed class FakeUnknownPayloadTool : ITool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "test.unknown",
            Name = "未知载荷测试",
            Category = "test",
            Accepts = InputShape.Text
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok(new UnknownPayload()));
    }
}
