// WorkspaceViewModel 分部类：工具台表面槽位（插件自定义视图）的生命周期。
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.Plugin.Ui;

namespace Sparrow.App.ViewModels;

public partial class WorkspaceViewModel
{
    /// <summary>
    /// 当前工具的自定义表面。为 <c>null</c> 时工具台使用宿主默认表面。
    ///
    /// 只在 UI 线程创建；切换工具 / 返回墙时先置空再从界面断开，
    /// 实现了 <see cref="IDisposable"/> 的视图会顺带释放。
    /// </summary>
    [ObservableProperty]
    private Control? _toolView;

    /// <summary>是否有插件自定义配置区；XAML 用它决定是否在输入/结果之上显示。</summary>
    public bool HasToolView => ToolView is not null;

    /// <summary>有自定义配置区时参数由视图自己负责，默认参数行隐藏，避免双份。</summary>
    public bool ShowParameters => HasParameters && !HasToolView;

    /// <summary>视图自己写入、但工具描述里没有声明的参数；执行时并入参数表。</summary>
    private readonly Dictionary<string, string> _viewParameters = new(StringComparer.Ordinal);

    partial void OnToolViewChanged(Control? value)
    {
        OnPropertyChanged(nameof(HasToolView));
        OnPropertyChanged(nameof(ShowParameters));
    }

    /// <summary>
    /// 重建表面槽位：先断开旧视图，再按当前工具尝试创建。
    /// 创建失败只回退默认表面，不影响工具台其余部分。
    /// </summary>
    private void RefreshToolView()
    {
        ClearToolView();

        if (!IsDetailOpen || SelectedTool?.Tool is not IToolViewFactory factory)
            return;

        // 控件只能在 UI 线程创建；不在 UI 线程时排到 UI 线程，本帧先保持默认表面
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshToolView);
            return;
        }

        try
        {
            ToolView = factory.CreateView(new ToolViewContext(this));
        }
        catch (Exception ex)
        {
            ToolView = null;
            _log.Error($"工具「{SelectedTool.Name}」的自定义界面创建失败，已回退默认表面", ex);
        }
    }

    /// <summary>断开并释放当前视图；没有视图时只清掉视图参数覆盖值。</summary>
    private void ClearToolView()
    {
        var view = ToolView;
        ToolView = null;
        _viewParameters.Clear();

        if (view is not IDisposable disposable)
            return;

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error("释放工具自定义界面失败", ex);
        }
    }

    /// <summary>读参数：先看工具声明的参数，再看视图自己写入的覆盖值。</summary>
    private string? GetViewParameter(string key)
    {
        foreach (var parameter in Parameters)
        {
            if (string.Equals(parameter.Key, key, StringComparison.Ordinal))
                return parameter.Value;
        }

        return _viewParameters.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// 写参数：声明的参数直接改 <see cref="ParameterViewModel"/>，
    /// 未声明的键先存覆盖值，执行时再并入 <c>ToolContext.Parameters</c>。
    /// </summary>
    private void SetViewParameter(string key, string? value)
    {
        foreach (var parameter in Parameters)
        {
            if (string.Equals(parameter.Key, key, StringComparison.Ordinal))
            {
                parameter.Value = value ?? string.Empty;
                return;
            }
        }

        _viewParameters[key] = value ?? string.Empty;
    }

    /// <summary>
    /// 输入文件快照：每次读取都重新取一份。视图不要缓存，
    /// 宿主拖入新文件后再次读取即可看到最新清单。
    /// </summary>
    private IReadOnlyList<string> GetViewInputFiles() => _droppedFiles.ToArray();

    /// <summary>
    /// 插件拿到的上下文。每次创建视图时新建一份，避免插件把旧工具的上下文缓存下来。
    /// </summary>
    private sealed class ToolViewContext : IToolViewContext
    {
        private readonly WorkspaceViewModel _owner;

        public ToolViewContext(WorkspaceViewModel owner) => _owner = owner;

        public string? GetParameter(string key) => _owner.GetViewParameter(key);

        public void SetParameter(string key, string? value) => _owner.SetViewParameter(key, value);

        public IReadOnlyList<string> InputFiles => _owner.GetViewInputFiles();
    }
}
