using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using Sparrow.App.Services;

namespace Sparrow.App.ViewModels;

/// <summary>文件搜索结果的一行。</summary>
public sealed class FileSearchItemViewModel
{
    public FileSearchItemViewModel(FileSearchItem item)
    {
        Name = item.Name;
        DirectoryPath = item.DirectoryPath;
        FullPath = item.FullPath;
        IsDirectory = item.IsDirectory;
    }

    public string Name { get; }

    public string DirectoryPath { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public bool IsFile => !IsDirectory;
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

        foreach (var item in results)
            Items.Add(new FileSearchItemViewModel(item));

        Selected = Items.FirstOrDefault();
        IsEmpty = Items.Count == 0;
        StatusText = BuildStatus(results.Count);
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
