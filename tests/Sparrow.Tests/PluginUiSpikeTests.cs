using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Plugins;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// P3 研究性测试：验证「插件自带 Avalonia 界面」的四条边界。
///
/// 结论（2026-11-28）：
///   1. 插件里的 UserControl 能被宿主直接显示 —— 可以。
///   2. avares:// 资源在独立 ALC 里能解析 —— 可以（能构造出视图即证明）。
///   3. 显示后 ALC 能否回收 —— **不能**。只加载、或创建视图但不显示，都能回收；
///      窗口 Show() 过之后回收不了，说明 Avalonia 的显示链路留下了静态引用。
///      P4 因此不承诺 UI 插件热卸载：禁用带界面的插件需要重启。
///   4. DynamicResource 能取到宿主资源 —— 可以。
///
/// 这个测试不改变正式 SDK；它只是把 D6 的问号变成可复现的断言。
/// </summary>
public sealed class PluginUiSpikeTests
{
    private const string PluginId = "sparrow.plugin.ui-spike";

    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public void 插件产物_不包含Avalonia副本()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "ui-spike");

        Directory.Exists(source).ShouldBeTrue("spike 插件产物没有复制到测试输出");

        var files = Directory.EnumerateFiles(source)
            .Select(Path.GetFileName)
            .ToList();

        files.ShouldContain(name => name == "Sparrow.Plugin.UiSpike.dll");
        files.ShouldNotContain(
            name => name!.StartsWith("Avalonia", StringComparison.Ordinal),
            "插件自带 Avalonia 会让 Control 变成两个类型");
    }

    [AvaloniaFact]
    public void 插件视图_能显示并使用宿主资源()
    {
        using var loader = new PluginLoader(new NullLog());
        var plugin = loader.Load(ReadCandidate(CreatePluginRoot())).ShouldNotBeNull();

        // 走插件自己的 CreateView：XAML 编译产物在插件程序集里，
        // 能构造出来就说明 avares:// 在独立 ALC 里解析成功。
        var control = CreateView(plugin);

        var window = new Window
        {
            Width = 360,
            Height = 240,
            Content = control
        };

        // 用宿主资源键验证 DynamicResource：插件不硬编码颜色
        window.Resources["TextPrimaryBrush"] = Brushes.Red;
        window.Resources["AccentSoftBrush"] = Brushes.Blue;

        try
        {
            window.Show();

            var text = control.FindControl<TextBlock>("SpikeText").ShouldNotBeNull();
            text.Text.ShouldBe("插件界面已加载");

            var foreground = text.Foreground.ShouldNotBeNull();
            ((ISolidColorBrush)foreground).Color.ShouldBe(Colors.Red);

            var badge = control.FindControl<Border>("SpikeBadge").ShouldNotBeNull();
            var background = badge.Background.ShouldNotBeNull();
            ((ISolidColorBrush)background).Color.ShouldBe(Colors.Blue);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void 仅加载不创建视图_ALC能回收()
    {
        var weak = LoadOnly(CreatePluginRoot());
        CollectUntilDead(weak);
        weak.IsAlive.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void 创建视图但不显示_ALC能回收()
    {
        var weak = LoadCreateView(CreatePluginRoot());
        CollectUntilDead(weak);
        weak.IsAlive.ShouldBeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadOnly(string pluginsRoot)
    {
        var loader = new PluginLoader(new NullLog());
        var plugin = loader.Load(ReadCandidate(pluginsRoot)).ShouldNotBeNull();
        plugin = null!;

        var weak = loader.Unload(PluginId).ShouldNotBeNull();
        loader.Dispose();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadCreateView(string pluginsRoot)
    {
        var loader = new PluginLoader(new NullLog());
        var plugin = loader.Load(ReadCandidate(pluginsRoot)).ShouldNotBeNull();

        var control = CreateView(plugin);
        control = null!;
        plugin = null!;

        var weak = loader.Unload(PluginId).ShouldNotBeNull();
        loader.Dispose();
        return weak;
    }

    private static void CollectUntilDead(WeakReference weak)
    {
        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static Control CreateView(LoadedPlugin plugin)
    {
        var tool = plugin.Tools.ShouldHaveSingleItem();
        var method = tool.GetType().GetMethod(
            "CreateView",
            BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull("spike 插件应暴露 CreateView()");
        return ((Control)method!.Invoke(tool, null)!).ShouldNotBeNull();
    }

    private static PluginCandidate ReadCandidate(string pluginsRoot)
    {
        var candidate = new PluginScanner(pluginsRoot, new NullLog())
            .Scan()
            .ShouldHaveSingleItem();

        candidate.IsValid.ShouldBeTrue(candidate.Error);
        return candidate;
    }

    private static string CreatePluginRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sparrow-tests",
            Guid.NewGuid().ToString("N"));

        var directory = Path.Combine(root, PluginId);
        Directory.CreateDirectory(directory);

        var source = Path.Combine(AppContext.BaseDirectory, "ui-spike");
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));

        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "id": "{{PluginId}}",
              "name": "UI Spike",
              "version": "1.0.0",
              "sdkVersion": "1.0",
              "entryAssembly": "Sparrow.Plugin.UiSpike.dll",
              "tools": ["spike.ui.view"]
            }
            """);

        return root;
    }
}
