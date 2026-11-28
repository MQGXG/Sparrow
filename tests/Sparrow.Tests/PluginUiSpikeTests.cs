using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using Shouldly;
using Sparrow.App;
using Sparrow.App.History;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
using Sparrow.Plugin.Ui;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// P3 / P4-A 的界面边界测试：验证「插件自带 Avalonia 界面」的四条边界，
/// 以及 P4-A 的表面槽位（自定义视图创建、回退与释放）。
///
/// P3 结论（2026-11-28）：
///   1. 插件里的 UserControl 能被宿主直接显示 —— 可以。
///   2. avares:// 资源在独立 ALC 里能解析 —— 可以（能构造出视图即证明）。
///   3. 显示后 ALC 能否回收 —— **不能**。只加载、或创建视图但不显示，都能回收；
///      窗口 Show() 过之后回收不了，说明 Avalonia 的显示链路留下了静态引用。
///      P4 因此不承诺 UI 插件热卸载：禁用带界面的插件需要重启。
///   4. DynamicResource 能取到宿主资源 —— 可以。
///
/// P4-A 结论：插件通过 <see cref="IToolViewFactory"/> 返回宿主控件组合，
/// 类型身份与宿主一致；创建失败时工具台回退默认表面。
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
        public void Add(HistoryEntry entry) { }
        public IReadOnlyList<HistoryEntry> Recent(int count) => [];
        public void Clear() { }
    }

    /// <summary>测试专用的视图上下文：只验证契约，不连真实参数存储。</summary>
    private sealed class TestViewContext : IToolViewContext
    {
        private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

        public string? GetParameter(string key) => _parameters.GetValueOrDefault(key);

        public void SetParameter(string key, string? value)
        {
            if (value is null)
                _parameters.Remove(key);
            else
                _parameters[key] = value;
        }

        public IReadOnlyList<string> InputFiles => [];
    }

    /// <summary>只用于验证宿主回归逻辑的假工厂；不引用任何插件命名空间。</summary>
    private sealed class FactoryTool(Func<Control?> create) : ITool, IToolViewFactory
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Id = "test.view.factory",
            Name = "视图工厂测试",
            Category = "test",
            Accepts = InputShape.Text,
            Produces = OutputShape.Text,
            Parameters =
            [
                new ToolParameter { Key = "start", Label = "起始时间" }
            ]
        };

        public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok(new TextPayload("view factory ok")));

        public Control? CreateView(IToolViewContext context) => create();
    }

    /// <summary>验证宿主在断开视图时会释放它。</summary>
    private sealed class DisposableView : Control, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public void 插件产物_不包含Avalonia或UI契约副本()
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
        files.ShouldNotContain(
            name => name == "Sparrow.Plugin.Ui.dll",
            "插件自带 UI 契约副本会让基类变成两个类型");
    }

    [AvaloniaFact]
    public void 插件视图_能显示并使用宿主资源()
    {
        using var loader = new PluginLoader(new NullLog());
        var plugin = loader.Load(ReadCandidate(CreatePluginRoot())).ShouldNotBeNull();

        // 走插件自己的 IToolViewFactory：XAML 编译产物在插件程序集里，
        // 能构造出来就说明 avares:// 在独立 ALC 里解析成功。
        var control = CreateView(plugin);

        var window = new Window
        {
            Width = 360,
            Height = 240,
            Content = control
        };

        // 宿主控件积木的模板由契约包提供，窗口必须显式挂样式
        window.Styles.Add(new StyleInclude(new Uri("avares://Sparrow.Plugin.Ui/"))
        {
            Source = new Uri("avares://Sparrow.Plugin.Ui/Themes/PluginControls.axaml")
        });

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

    [AvaloniaFact]
    public void 数字输入控件_值与内部输入框双向同步()
    {
        var field = new PluginNumberField
        {
            Label = "开始时间",
            Value = 12,
            Minimum = 0,
            Maximum = 600
        };

        var window = new Window
        {
            Width = 300,
            Height = 200,
            Content = field
        };

        window.Styles.Add(new StyleInclude(new Uri("avares://Sparrow.Plugin.Ui/"))
        {
            Source = new Uri("avares://Sparrow.Plugin.Ui/Themes/PluginControls.axaml")
        });

        try
        {
            window.Show();

            var input = field.GetVisualDescendants()
                .OfType<NumericUpDown>()
                .ShouldHaveSingleItem();

            input.Value.ShouldBe(12m);

            input.Value = 34m;
            field.Value.ShouldBe(34m);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void spike工具_实现IToolViewFactory_并组合宿主控件()
    {
        using var loader = new PluginLoader(new NullLog());
        var plugin = loader.Load(ReadCandidate(CreatePluginRoot())).ShouldNotBeNull();

        var tool = plugin.Tools.ShouldHaveSingleItem();
        var factory = tool.ShouldBeAssignableTo<IToolViewFactory>();

        var context = new TestViewContext();
        context.SetParameter("start", "12");

        var view = factory.CreateView(context).ShouldNotBeNull();

        // 类型身份证明：插件 XAML 里的控件与宿主是同一批类型，所以这里能 OfType
        view.GetLogicalDescendants().OfType<PluginSection>().ShouldNotBeEmpty();
        view.GetLogicalDescendants().OfType<PluginHint>().ShouldNotBeEmpty();
        view.GetLogicalDescendants().OfType<PluginNumberField>().ShouldNotBeEmpty();

        context.GetParameter("start").ShouldBe("12");
    }

    [AvaloniaFact]
    public void 自定义视图_打开工具时创建_返回墙时释放()
    {
        var tool = new FactoryTool(() => new DisposableView());
        var (viewModel, catalog) = CreateWorkspace(tool);

        using (catalog)
        {
            viewModel.HasToolView.ShouldBeTrue();
            viewModel.ToolView.ShouldBeOfType<DisposableView>();
            viewModel.ShowParameters.ShouldBeFalse("自定义视图自己负责参数 UI");

            var view = (DisposableView)viewModel.ToolView!;

            viewModel.CloseDetail();

            viewModel.ToolView.ShouldBeNull();
            viewModel.HasToolView.ShouldBeFalse();
            viewModel.ShowParameters.ShouldBeTrue("回到默认表面后参数行应恢复");
            view.IsDisposed.ShouldBeTrue();
        }
    }

    [AvaloniaFact]
    public void 自定义视图_创建失败时回退默认表面()
    {
        var tool = new FactoryTool(() => throw new InvalidOperationException("boom"));
        var (viewModel, catalog) = CreateWorkspace(tool);

        using (catalog)
        {
            viewModel.ToolView.ShouldBeNull();
            viewModel.HasToolView.ShouldBeFalse();
            viewModel.ShowParameters.ShouldBeTrue();
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
        var factory = tool.ShouldBeAssignableTo<IToolViewFactory>();
        return factory.CreateView(new TestViewContext()).ShouldNotBeNull();
    }

    /// <summary>构造一个最小工作区并打开假工具，专门验证表面槽位生命周期。</summary>
    private static (WorkspaceViewModel ViewModel, ToolCatalog Catalog) CreateWorkspace(ITool tool)
    {
        var log = new NullLog();
        var settings = new AppSettings();
        var store = new SettingsStore(
            Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N") + ".json"),
            log);

        var catalog = new ToolCatalog(
            Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N")),
            log,
            settings);

        var settingsViewModel = new SettingsViewModel(
            catalog,
            store,
            new StartupService(log),
            settings);

        var viewModel = new WorkspaceViewModel(
            catalog,
            new FakeClipboard(),
            log,
            new FakeHistory(),
            settingsViewModel,
            loadClipboardOnCreate: false);

        viewModel.OpenTool(new ToolItemViewModel(new ToolEntry
        {
            Tool = tool,
            Host = new ToolHost("test.workspace", Path.GetTempPath(), log)
        }));

        return (viewModel, catalog);
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
