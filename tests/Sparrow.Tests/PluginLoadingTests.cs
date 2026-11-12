using System.Runtime.CompilerServices;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 插件发现 → 加载 → 注册表的完整链路。
///
/// 测试用临时目录 + 测试输出目录里的真实插件 dll，
/// 而不是直接读仓库里的 plugins/，这样路径不依赖构建配置。
/// </summary>
public sealed class PluginLoadingTests
{
    private const string ValidManifest = """
        {
          "id": "sparrow.plugin.json-format",
          "name": "JSON 格式化",
          "version": "1.0.0",
          "sdkVersion": "1.0",
          "entryAssembly": "Sparrow.Plugin.Json.Format.dll",
          "tools": ["sparrow.json.format"]
        }
        """;

    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    /// <summary>用完即弃的插件根目录。</summary>
    private sealed class TempPluginRoot : IDisposable
    {
        public string Path { get; }

        public TempPluginRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string CreatePlugin(
            string directoryName,
            string manifestJson,
            bool copyEntryAssembly = true,
            bool copySdk = false)
        {
            var directory = System.IO.Path.Combine(Path, directoryName);
            Directory.CreateDirectory(directory);

            File.WriteAllText(System.IO.Path.Combine(directory, "plugin.json"), manifestJson);

            if (copyEntryAssembly)
            {
                foreach (var file in new[] { "Sparrow.Plugin.Json.Format.dll", "Sparrow.Plugin.Json.Format.deps.json" })
                {
                    var source = System.IO.Path.Combine(AppContext.BaseDirectory, file);
                    if (File.Exists(source))
                        File.Copy(source, System.IO.Path.Combine(directory, file));
                }
            }

            // 故意把 SDK 放进插件目录：验证装载器会拦下它，而不是静默加载出第二个 ITool
            if (copySdk)
            {
                var sdk = System.IO.Path.Combine(AppContext.BaseDirectory, "Sparrow.Plugin.Sdk.dll");
                if (File.Exists(sdk))
                    File.Copy(sdk, System.IO.Path.Combine(directory, "Sparrow.Plugin.Sdk.dll"));
            }

            return directory;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // ALC 可能还持有 dll 句柄；临时目录留给系统清理，不影响测试结论
            }
        }
    }

    private static PluginScanner Scanner(TempPluginRoot root) => new(root.Path, new NullLog());

    [Fact]
    public void 扫描_有效清单_返回可用候选()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest);

        var candidates = Scanner(root).Scan();

        candidates.Count.ShouldBe(1);
        candidates[0].IsValid.ShouldBeTrue();
        candidates[0].Manifest!.Tools.ShouldBe(["sparrow.json.format"]);
    }

    [Fact]
    public void 扫描_目录不存在_返回空集合而不是抛异常()
    {
        var scanner = new PluginScanner(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N")),
            new NullLog());

        scanner.Scan().ShouldBeEmpty();
    }

    [Fact]
    public void 扫描_清单id与目录名不一致_标记无效()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("wrong-directory-name", ValidManifest);

        var candidate = Scanner(root).Scan().ShouldHaveSingleItem();

        candidate.IsValid.ShouldBeFalse();
        candidate.Error!.ShouldContain("目录名");
    }

    [Fact]
    public void 扫描_tools为空_标记无效()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest.Replace(
            """["sparrow.json.format"]""", "[]"));

        var candidate = Scanner(root).Scan().ShouldHaveSingleItem();

        candidate.IsValid.ShouldBeFalse();
        candidate.Error!.ShouldContain("tools");
    }

    [Fact]
    public void 扫描_入口dll缺失_标记无效()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest, copyEntryAssembly: false);

        var candidate = Scanner(root).Scan().ShouldHaveSingleItem();

        candidate.IsValid.ShouldBeFalse();
        candidate.Error!.ShouldContain("入口程序集");
    }

    [Fact]
    public void 扫描_SDK主版本不兼容_标记无效()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest.Replace(
            "\"sdkVersion\": \"1.0\"", "\"sdkVersion\": \"2.0\""));

        var candidate = Scanner(root).Scan().ShouldHaveSingleItem();

        candidate.IsValid.ShouldBeFalse();
        candidate.Error!.ShouldContain("SDK");
    }

    [Fact]
    public void 扫描_清单JSON语法错误_标记无效()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", "{ 这不是 JSON ");

        var candidate = Scanner(root).Scan().ShouldHaveSingleItem();

        candidate.IsValid.ShouldBeFalse();
        candidate.Error!.ShouldContain("清单解析失败");
    }

    [Fact]
    public void 加载_真实插件_ITool类型一致且来自插件ALC()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest);

        var candidate = Scanner(root).Scan().ShouldHaveSingleItem();
        using var loader = new PluginLoader(new NullLog());

        var plugin = loader.Load(candidate);

        plugin.ShouldNotBeNull();
        plugin!.Tools.Count.ShouldBe(1);

        var tool = plugin.Tools[0];

        // 铁律 1：ITool 必须能被宿主识别
        tool.ShouldBeAssignableTo<ITool>();
        tool.Descriptor.Id.ShouldBe("sparrow.json.format");

        // 插件类型来自它自己的 ALC，而不是宿主默认上下文
        tool.GetType().Assembly.ShouldNotBeSameAs(typeof(ITool).Assembly);
    }

    [Fact]
    public void 加载_插件目录里混入SDK_dll_仍能正确识别ITool()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest, copySdk: true);

        var candidate = Scanner(root).Scan().ShouldHaveSingleItem();
        using var loader = new PluginLoader(new NullLog());

        var plugin = loader.Load(candidate);

        // 装载器必须拦下 SDK，否则这里会拿到 null（静默失败）
        plugin.ShouldNotBeNull();
        plugin!.Tools.ShouldHaveSingleItem().ShouldBeAssignableTo<ITool>();
    }

    [Fact]
    public void 加载_无效候选_返回null()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("wrong-directory-name", ValidManifest);

        var candidate = Scanner(root).Scan().ShouldHaveSingleItem();
        using var loader = new PluginLoader(new NullLog());

        loader.Load(candidate).ShouldBeNull();
    }

    [Fact]
    public void 工具目录_构造时不加载_首次取注册表才构建()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest);

        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());

        // 懒加载是结构性的：构造完不应该已经扫描/加载
        catalog.IsBuilt.ShouldBeFalse();

        var registry = catalog.GetRegistry();

        catalog.IsBuilt.ShouldBeTrue();
        registry.Search("json").ShouldHaveSingleItem().Descriptor.Id.ShouldBe("sparrow.json.format");
    }

    [Fact]
    public void 工具目录_空目录_注册表为空但可正常构建()
    {
        using var root = new TempPluginRoot();
        using var catalog = new ToolCatalog(root.Path, new NullLog(), new AppSettings());

        var registry = catalog.GetRegistry();

        registry.Count.ShouldBe(0);
        registry.Search("json").ShouldBeEmpty();
    }

    [Fact]
    public void 禁用的插件_不加载也不进注册表()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest);

        var settings = new AppSettings { DisabledPlugins = ["sparrow.plugin.json-format"] };
        using var catalog = new ToolCatalog(root.Path, new NullLog(), settings);

        var registry = catalog.GetRegistry();

        registry.Count.ShouldBe(0);
        registry.Search("json").ShouldBeEmpty();
    }

    [Fact]
    public void 卸载插件_ALC能被回收()
    {
        using var root = new TempPluginRoot();
        root.CreatePlugin("sparrow.plugin.json-format", ValidManifest);

        var weak = LoadThenUnload(root.Path);

        // ALC 卸载是异步的，需要多轮 GC 把终结器队列也排空
        for (var i = 0; i < 5 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        // 铁律 4：卸载后加载上下文必须真的被回收
        weak.IsAlive.ShouldBeFalse();
    }

    /// <summary>
    /// 刻意 NoInlining：如果加载/卸载写在测试方法体内，
    /// Debug 构建的 JIT 会让局部变量（尤其是注册表）活到方法结束之后，
    /// ALC 就永远回收不了，测试会假失败。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadThenUnload(string pluginsRoot)
    {
        using var catalog = new ToolCatalog(pluginsRoot, new NullLog(), new AppSettings());

        // 不保存注册表引用：它会持有 ITool 实例，阻止 ALC 回收
        catalog.GetRegistry().Count.ShouldBe(1);

        var weak = catalog.UnloadPlugin("sparrow.plugin.json-format");
        weak.ShouldNotBeNull();

        // 注册表同步移除
        catalog.GetRegistry().Count.ShouldBe(0);

        return weak!;
    }
}
