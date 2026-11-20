using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 设置页里的一行本地模型：清单信息 + 下载进度 + 已下载状态。
///
/// 下载、校验、落盘都在 <see cref="ILocalModels"/>（宿主）；
/// 这里只负责把进度搬到 UI 上，并告诉设置页刷新状态。
/// </summary>
public partial class LocalModelItemViewModel : ObservableObject
{
    private readonly ILocalModels _models;
    private readonly Action<string> _report;

    public LocalModelItemViewModel(
        LocalModelInfo info,
        ILocalModels models,
        Action<string> report)
    {
        Info = info;
        _models = models;
        _report = report;
        _isInstalled = models.IsInstalled(info.Id);
    }

    public LocalModelInfo Info { get; }

    public string Name => Info.Name;

    public string Description => Info.Description;

    public string SizeText => FormatBytes(Info.SizeBytes);

    public string MemoryText => $"{Info.MemoryGb} GB 内存";

    public string License => Info.License;

    /// <summary>没有下载源的模型（例如 TranslateGemma 还没有稳定直链）不允许点下载。</summary>
    public bool HasSource => Info.DownloadUrls.Count > 0;

    public string StateText => IsInstalled
        ? "已下载"
        : HasSource ? "未下载" : "暂未配置下载源";

    public bool ShowDownload => !IsInstalled && HasSource;

    public bool ShowDelete => IsInstalled;

    /// <summary>下载中禁用按钮，避免重复点击起两个下载。</summary>
    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>0..1，来自宿主的上报。</summary>
    [ObservableProperty]
    private double _progress;

    public double ProgressPercent => Progress * 100;

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ShowDownload));
        OnPropertyChanged(nameof(ShowDelete));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressPercent));

    /// <summary>下载（已下载则直接返回）。失败只提示，不抛异常打穿设置页。</summary>
    public async Task EnsureAsync()
    {
        if (IsBusy || IsInstalled)
            return;

        if (!HasSource)
        {
            _report($"「{Name}」暂未配置下载源");
            return;
        }

        IsBusy = true;
        Progress = 0;

        try
        {
            // Progress 在 UI 线程创建，回调会回到 UI 线程
            var progress = new Progress<double>(value => Progress = value);
            var path = await _models.EnsureAsync(Info.Id, progress, CancellationToken.None);

            if (path is null)
            {
                _report($"「{Name}」下载失败，请检查网络或稍后再试");
                return;
            }

            IsInstalled = true;
            _report($"「{Name}」已下载完成");
        }
        catch (Exception ex)
        {
            // 事件处理器是 async void，异常漏出去会直接把进程带崩
            _report($"「{Name}」下载失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    /// <summary>删除已下载的模型，释放磁盘。</summary>
    public async Task DeleteAsync()
    {
        if (IsBusy || !IsInstalled)
            return;

        IsBusy = true;

        try
        {
            if (await _models.DeleteAsync(Info.Id, CancellationToken.None))
            {
                IsInstalled = false;
                _report($"已删除「{Name}」");
            }
            else
            {
                _report($"删除「{Name}」失败，可能仍在使用中，请稍后再试");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.#} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        _ => $"{bytes / 1000.0:0.#} KB"
    };
}
