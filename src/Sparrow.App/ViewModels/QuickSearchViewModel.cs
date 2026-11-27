using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Services;

namespace Sparrow.App.ViewModels;

/// <summary>文件搜索结果的一行。</summary>
public partial class FileSearchItemViewModel : ObservableObject
{
    public FileSearchItemViewModel(FileSearchItem item, string highlightTerm)
    {
        Name = item.Name;
        DirectoryPath = item.DirectoryPath;
        FullPath = item.FullPath;
        IsDirectory = item.IsDirectory;
        Size = item.Size;
        Modified = item.Modified;
        HighlightTerm = highlightTerm;
    }

    public string Name { get; }

    public string DirectoryPath { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public long Size { get; }

    public DateTime Modified { get; }

    public string HighlightTerm { get; }

    public bool IsFile => !IsDirectory;

    /// <summary>索引没有带回元数据时不显示大小/时间列。</summary>
    public bool HasDetails => Modified != default;

    public string SizeText => IsDirectory || !HasDetails ? string.Empty : FormatSize(Size);

    public string ModifiedText => HasDetails ? Modified.ToString("MM-dd HH:mm") : string.Empty;

    /// <summary>Shell 文件类型图标；后台加载，未完成时为 null（显示几何占位）。</summary>
    [ObservableProperty]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}

/// <summary>
/// 双击 Ctrl 唤出的文件搜索面板。
///
/// 只做三件事：拿到查询词、向索引要结果、维护选中项。
/// 打开文件和「打开所在位置」属于窗口行为，放在 code-behind。
/// </summary>
public partial class QuickSearchViewModel : ObservableObject
{
    /// <summary>一次最多显示多少条，避免结果区无限长。</summary>
    private const int MaxResults = 100;

    private readonly IFileIndex _index;
    private readonly DispatcherTimer _debounce;

    /// <summary>查询版本号：只有最后一次查询的结果才允许写回 UI。</summary>
    private int _version;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private FileSearchItemViewModel? _selected;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    public ObservableCollection<FileSearchItemViewModel> Items { get; } = [];

    public QuickSearchViewModel(IFileIndex index)
    {
        _index = index;
        _index.Changed += OnIndexChanged;

        // 索引可能有几十万条，每敲一个字符都全量扫会卡输入：先停 120ms 再查
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RefreshAsync();
        };
    }

    /// <summary>每次唤出时清空查询并重新取一次结果。</summary>
    public void Reset()
    {
        Query = string.Empty;
        _debounce.Stop();
        RefreshAsync();
    }

    /// <summary>重新执行当前查询；右键菜单执行删除/重命名后刷新结果。</summary>
    public void Refresh() => RefreshAsync();

    partial void OnQueryChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// 在后台线程查询，避免 178 万条索引的冷查询卡住 UI 线程。
    /// 只允许最后一次查询的结果回写，防止快速输入时旧结果覆盖新结果。
    /// </summary>
    private async void RefreshAsync()
    {
        var version = Interlocked.Increment(ref _version);
        var query = Query;

        IReadOnlyList<FileSearchItem> results;

        try
        {
            results = await Task.Run(() => _index.Search(query, MaxResults));
        }
        catch
        {
            return;
        }

        if (version != Volatile.Read(ref _version))
            return;

        Items.Clear();

        var views = new List<FileSearchItemViewModel>(results.Count);

        foreach (var item in results)
        {
            var view = new FileSearchItemViewModel(item, query);
            views.Add(view);
            Items.Add(view);
        }

        Selected = Items.FirstOrDefault();
        IsEmpty = Items.Count == 0;
        StatusText = BuildStatus(results.Count);

        // 图标在后台按扩展名加载；同一类型命中缓存后几乎无开销
        _ = LoadIconsAsync(views);
    }

    private static async Task LoadIconsAsync(
        IReadOnlyList<FileSearchItemViewModel> items)
    {
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                var icon = ShellIconProvider.GetIcon(item.FullPath, item.IsDirectory);

                if (icon is null)
                    continue;

                Dispatcher.UIThread.Post(() => item.Icon = icon);
            }
        });
    }

    private string BuildStatus(int shown)
    {
        if (!string.IsNullOrEmpty(_index.StatusMessage))
            return _index.StatusMessage;

        if (_index.IsIndexing)
            return $"正在建立索引… 已收录 {_index.Count:N0} 个文件";

        if (!_index.IsReady)
            return "准备索引…";

        if (string.IsNullOrWhiteSpace(Query))
            return $"已索引 {_index.Count:N0} 个文件，输入文件名开始搜索";

        return shown == 0
            ? "没有匹配的文件"
            : $"{shown} 个结果（索引共 {_index.Count:N0} 个文件）";
    }

    private void OnIndexChanged(object? sender, EventArgs e)
    {
        // 索引在后台线程完成，结果必须回到 UI 线程更新
        Dispatcher.UIThread.Post(() => RefreshAsync());
    }
}
