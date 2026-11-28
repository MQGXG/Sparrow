using Shouldly;
using Sparrow.App;
using Sparrow.App.Settings;
using Xunit;

namespace Sparrow.Tests;

public sealed class SettingsStoreTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class TempSettingsFile : IDisposable
    {
        public string Path { get; }

        public TempSettingsFile()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sparrow-tests",
                Guid.NewGuid().ToString("N"),
                "settings.json");
        }

        public void Write(string content)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, content);
        }

        public void Dispose()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (directory is not null && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }

    [Fact]
    public void 文件不存在_返回默认设置()
    {
        using var file = new TempSettingsFile();
        var store = new SettingsStore(file.Path, new NullLog());

        var settings = store.Load();

        settings.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        settings.DisabledPlugins.ShouldBeEmpty();
    }

    [Fact]
    public void 保存后能原样读回()
    {
        using var file = new TempSettingsFile();
        var store = new SettingsStore(file.Path, new NullLog());

        store.Save(new AppSettings { DisabledPlugins = ["sparrow.plugin.dev-base64"] }).ShouldBeTrue();

        store.Load().DisabledPlugins.ShouldBe(["sparrow.plugin.dev-base64"]);
    }

    [Fact]
    public void 文件损坏_回退默认值而不是抛异常()
    {
        using var file = new TempSettingsFile();
        file.Write("{ 这不是 JSON ");
        var store = new SettingsStore(file.Path, new NullLog());

        var settings = store.Load();

        settings.SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
        settings.DisabledPlugins.ShouldBeEmpty();
    }

    [Fact]
    public void 空文件_回退默认值()
    {
        using var file = new TempSettingsFile();
        file.Write(string.Empty);
        var store = new SettingsStore(file.Path, new NullLog());

        store.Load().DisabledPlugins.ShouldBeEmpty();
    }

    [Fact]
    public void 保存_先写临时文件再替换_不留下半截或临时文件()
    {
        using var file = new TempSettingsFile();
        var store = new SettingsStore(file.Path, new NullLog());

        store.Save(new AppSettings()).ShouldBeTrue();

        File.Exists(file.Path).ShouldBeTrue();
        File.Exists(file.Path + ".tmp").ShouldBeFalse();
        store.Load().SchemaVersion.ShouldBe(AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public void 插件设置_写盘后能读回()
    {
        using var file = new TempSettingsFile();
        var store = new SettingsStore(file.Path, new NullLog());
        var settings = new AppSettings();
        var plugin = new PluginSettings("sparrow.plugin.test", settings.Plugins, store);

        plugin.Set("endpoint", "http://localhost:11434");
        plugin.Set("count", "3");

        var loaded = store.Load();
        loaded.Plugins["sparrow.plugin.test"]["endpoint"].ShouldBe("http://localhost:11434");
        loaded.Plugins["sparrow.plugin.test"]["count"].ShouldBe("3");
        plugin.Get("endpoint").ShouldBe("http://localhost:11434");
    }

    [Fact]
    public void 插件设置_删除最后一个键后移除空节点()
    {
        using var file = new TempSettingsFile();
        var store = new SettingsStore(file.Path, new NullLog());
        var settings = new AppSettings();
        var plugin = new PluginSettings("sparrow.plugin.test", settings.Plugins, store);

        plugin.Set("key", "value");
        plugin.Set("key", null);

        plugin.Get("key").ShouldBeNull();
        store.Load().Plugins.ContainsKey("sparrow.plugin.test").ShouldBeFalse();
    }

    [Fact]
    public void 插件设置_不覆盖磁盘上的普通设置()
    {
        using var file = new TempSettingsFile();
        var store = new SettingsStore(file.Path, new NullLog());
        store.Save(new AppSettings { Appearance = "light" }).ShouldBeTrue();

        var settings = new AppSettings();
        var plugin = new PluginSettings("sparrow.plugin.test", settings.Plugins, store);
        plugin.Set("key", "value");

        var loaded = store.Load();
        loaded.Appearance.ShouldBe("light");
        loaded.Plugins["sparrow.plugin.test"]["key"].ShouldBe("value");
    }
}
