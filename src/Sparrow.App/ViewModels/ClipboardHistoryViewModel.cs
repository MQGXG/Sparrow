using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>剪贴板页的筛选 chip（类型或标签）。</summary>
public sealed partial class ClipboardFilterViewModel : ObservableObject
{
    public ClipboardFilterViewModel(string name, bool isAll = false)
    {
        Name = name;
        IsAll = isAll;
    }

    public string Name { get; }

    /// <summary>「全部」/「全部标签」：不是具体筛选值。</summary>
    public bool IsAll { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 剪贴板历史页。
///
/// 宿主持有数据与监听（<see cref="ClipboardHistoryStore"/>），这里只做浏览、筛选和
/// 收藏/标签/删除这些数据操作——不涉及 Win32，也不复制文件。
/// </summary>
public sealed partial class ClipboardHistoryViewModel : ObservableObject
{
    private const int MaxLoad = 1000;

    private const string KindAll = "全部";
    private const string KindText = "文本";
    private const string KindLink = "链接";
    private const string KindImage = "图片";
    private const string KindFiles = "文件";
    private const string KindFavorite = "收藏";

    private readonly ClipboardHistoryStore _store;
    private readonly IClipboardAccess _clipboard;
    private readonly SynchronizationContext? _ui;
    private bool _refreshing;

    public ClipboardHistoryViewModel(ClipboardHistoryStore store, IClipboardAccess clipboard)
    {
        _store = store;
        _clipboard = clipboard;
        _ui = SynchronizationContext.Current;

        Kinds =
        [
            new ClipboardFilterViewModel(KindAll, isAll: true) { IsSelected = true },
            new ClipboardFilterViewModel(KindText),
            new ClipboardFilterViewModel(KindLink),
            new ClipboardFilterViewModel(KindImage),
            new ClipboardFilterViewModel(KindFiles),
            new ClipboardFilterViewModel(KindFavorite)
        ];

        _store.Changed += OnStoreChanged;
        Refresh();
    }

    /// <summary>类型筛选 chips。</summary>
    public IReadOnlyList<ClipboardFilterViewModel> Kinds { get; }

    /// <summary>手动标签筛选 chips；第一个是「全部标签」。</summary>
    public ObservableCollection<ClipboardFilterViewModel> TagFilters { get; } = [];

    public ObservableCollection<ClipboardEntryViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _selectedKind = KindAll;

    [ObservableProperty]
    private string? _selectedTag;

    [ObservableProperty]
    private ClipboardEntryViewModel? _selectedItem;

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string? _detailText;

    [ObservableProperty]
    private byte[]? _detailImagePng;

    [ObservableProperty]
    private string? _detailPaths;

    [ObservableProperty]
    private string _newTag = string.Empty;

    [ObservableProperty]
    private bool _isClearConfirmVisible;

    public bool HasDetail => SelectedItem is not null;

    public bool HasDetailText => !string.IsNullOrEmpty(DetailText);

    public bool HasDetailImage => DetailImagePng is { Length: > 0 };

    public bool HasDetailPaths => !string.IsNullOrEmpty(DetailPaths);

    public bool IsEmpty => !HasItems;

    public bool HasTagFilters => TagFilters.Count > 1;

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnSelectedItemChanged(ClipboardEntryViewModel? oldValue, ClipboardEntryViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;

        if (newValue is not null)
            newValue.IsSelected = true;

        LoadDetail();
        OnPropertyChanged(nameof(HasDetail));
    }

    partial void OnDetailTextChanged(string? value) => OnPropertyChanged(nameof(HasDetailText));

    partial void OnDetailImagePngChanged(byte[]? value) => OnPropertyChanged(nameof(HasDetailImage));

    partial void OnDetailPathsChanged(string? value) => OnPropertyChanged(nameof(HasDetailPaths));

    partial void OnHasItemsChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public void SelectKind(ClipboardFilterViewModel filter)
    {
        SelectedKind = filter.IsAll ? KindAll : filter.Name;

        foreach (var kind in Kinds)
            kind.IsSelected = kind == filter;

        Refresh();
    }

    public void SelectTagFilter(ClipboardFilterViewModel filter)
    {
        SelectedTag = filter.IsAll ? null : filter.Name;

        foreach (var tag in TagFilters)
            tag.IsSelected = tag == filter;

        Refresh();
    }

    /// <summary>重新读取并筛选。订阅自 store.Changed 时会切到 UI 线程。</summary>
    public void Refresh()
    {
        if (_refreshing)
            return;

        _refreshing = true;

        try
        {
            var entries = string.IsNullOrWhiteSpace(Query)
                ? _store.Recent(MaxLoad)
                : _store.Search(Query, MaxLoad);

            var filtered = entries.Where(Matches).OrderByDescending(entry => entry.Favorite);
            var previousId = SelectedItem?.Id;

            Items.Clear();
            foreach (var entry in filtered)
                Items.Add(new ClipboardEntryViewModel(entry, _store));

            HasItems = Items.Count > 0;
            Summary = Query.Length > 0
                ? $"匹配 {Items.Count} 条"
                : $"共 {_store.Count} 条，显示 {Items.Count} 条";

            SyncTagFilters();

            SelectedItem = Items.FirstOrDefault(item => item.Id == previousId)
                ?? Items.FirstOrDefault();

            LoadDetail();
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void ToggleFavorite(ClipboardEntryViewModel item)
    {
        _store.SetFavorite(item.Id, !item.IsFavorite);
    }

    public void Delete(ClipboardEntryViewModel item)
    {
        if (SelectedItem?.Id == item.Id)
            SelectedItem = null;

        _store.Delete(item.Id);
    }

    public void Copy(ClipboardEntryViewModel item)
    {
        switch (item.Entry.Kind)
        {
            case ClipboardEntryKind.Text when item.Entry.Text is { } text:
                _clipboard.SetText(text);
                break;

            case ClipboardEntryKind.Image when _store.LoadImage(item.Entry) is { } png:
                _clipboard.SetImagePng(png);
                break;

            case ClipboardEntryKind.Files:
                _clipboard.SetText(string.Join(Environment.NewLine, item.Entry.Paths));
                break;
        }
    }

    /// <summary>给当前选中条目加一个手动标签。</summary>
    public void AddTag(string tag)
    {
        if (SelectedItem is null || string.IsNullOrWhiteSpace(tag))
            return;

        var tags = SelectedItem.Entry.Tags
            .Append(tag.Trim())
            .ToArray();

        var id = SelectedItem.Id;
        _store.SetTags(id, tags);
        NewTag = string.Empty;

        // store 的 Changed 会刷新列表；显式选回同一条，别让详情跳走
        SelectedItem = Items.FirstOrDefault(item => item.Id == id) ?? SelectedItem;
    }

    public void RemoveTag(ClipboardEntryViewModel item, string tag)
    {
        var tags = item.Entry.Tags
            .Where(existing => !string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var id = item.Id;
        _store.SetTags(id, tags);
        SelectedItem = Items.FirstOrDefault(entry => entry.Id == id) ?? SelectedItem;
    }

    public void RequestClear() => IsClearConfirmVisible = true;

    public void CancelClear() => IsClearConfirmVisible = false;

    public void ConfirmClear()
    {
        IsClearConfirmVisible = false;
        _store.Clear();
    }

    private bool Matches(ClipboardHistoryEntry entry)
    {
        var kindMatches = SelectedKind switch
        {
            KindText => entry.Kind == ClipboardEntryKind.Text,
            KindLink => entry.AutoTags.Contains(ClipboardClassifier.TagLink, StringComparer.OrdinalIgnoreCase),
            KindImage => entry.Kind == ClipboardEntryKind.Image,
            KindFiles => entry.Kind == ClipboardEntryKind.Files,
            KindFavorite => entry.Favorite,
            _ => true
        };

        if (!kindMatches)
            return false;

        return SelectedTag is null
            || entry.Tags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase)
            || entry.AutoTags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase);
    }

    private void SyncTagFilters()
    {
        var all = _store.AllTags();
        var selectedName = SelectedTag;

        // 只在标签集合变化时重建，避免每秒刷新都把用户选中的 chip 闪没
        var current = TagFilters.Skip(1).Select(filter => filter.Name).ToArray();
        if (current.SequenceEqual(all, StringComparer.OrdinalIgnoreCase))
            return;

        TagFilters.Clear();
        TagFilters.Add(new ClipboardFilterViewModel("全部标签", isAll: true)
        {
            IsSelected = selectedName is null
        });

        foreach (var tag in all)
        {
            TagFilters.Add(new ClipboardFilterViewModel(tag)
            {
                IsSelected = string.Equals(tag, selectedName, StringComparison.OrdinalIgnoreCase)
            });
        }

        OnPropertyChanged(nameof(HasTagFilters));
    }

    private void LoadDetail()
    {
        DetailText = null;
        DetailImagePng = null;
        DetailPaths = null;

        var entry = SelectedItem?.Entry;
        if (entry is null)
            return;

        switch (entry.Kind)
        {
            case ClipboardEntryKind.Text:
                DetailText = entry.Text ?? "（内容过长，只保留了摘要）";
                break;

            case ClipboardEntryKind.Image:
                DetailImagePng = _store.LoadImage(entry);
                break;

            case ClipboardEntryKind.Files:
                DetailPaths = string.Join(Environment.NewLine, entry.Paths);
                break;
        }
    }

    private void OnStoreChanged()
    {
        // 监听线程写历史；页面更新必须回到 UI 线程
        if (_ui is null)
        {
            Refresh();
            return;
        }

        _ui.Post(_ => Refresh(), null);
    }
}
