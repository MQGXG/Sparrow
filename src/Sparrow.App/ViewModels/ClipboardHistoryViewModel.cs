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
    private const string SourceAll = "全部来源";

    private readonly ClipboardHistoryStore _store;
    private readonly IClipboardAccess _clipboard;
    private readonly ClipboardPasteService? _pasteService;
    private readonly IClipboardExclusionStore? _exclusions;
    private readonly SynchronizationContext? _ui;
    private readonly List<(ClipboardHistoryEntry Entry, byte[]? Png)> _deleted = [];
    private bool _refreshing;

    public ClipboardHistoryViewModel(
        ClipboardHistoryStore store,
        IClipboardAccess clipboard,
        ClipboardPasteService? pasteService = null,
        IClipboardExclusionStore? exclusions = null)
    {
        _store = store;
        _clipboard = clipboard;
        _pasteService = pasteService;
        _exclusions = exclusions;
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

    /// <summary>顺序粘贴队列；顺序就是列表里的编号。</summary>
    public ObservableCollection<ClipboardQueueItemViewModel> Queue { get; } = [];

    /// <summary>按时间分组的列表视图：收藏 / 今天 / 三日内 / 一周内 / 更早。</summary>
    public ObservableCollection<ClipboardGroupViewModel> Groups { get; } = [];

    /// <summary>来源应用筛选（第一个是「全部来源」）。</summary>
    public ObservableCollection<string> SourceFilters { get; } = [];

    public bool HasQueue => Queue.Count > 0;

    public bool CanUndoDelete => _deleted.Count > 0;

    public string QueueHint => HasQueue
        ? $"队列 {Queue.Count} 条 · 在目标应用按全局热键粘贴下一条"
        : string.Empty;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _selectedKind = KindAll;

    [ObservableProperty]
    private string? _selectedTag;

    /// <summary>来源应用筛选；「全部来源」表示不过滤。</summary>
    [ObservableProperty]
    private string _selectedSource = SourceAll;

    /// <summary>操作反馈（排除/恢复记录）；空串表示不显示。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>来源行的「不记录」按钮；没选具体应用时禁用。</summary>
    public bool CanToggleExclusion => _exclusions is not null && SelectedSource != SourceAll;

    public string ExclusionButtonLabel
    {
        get
        {
            if (SelectedSource == SourceAll || _exclusions is null)
                return "不记录此应用";

            var excluded = _exclusions.ExcludedApps.Contains(SelectedSource, StringComparer.OrdinalIgnoreCase);
            return excluded ? $"恢复记录 {SelectedSource}" : $"不记录 {SelectedSource}";
        }
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnSelectedSourceChanged(string value)
    {
        StatusText = string.Empty;
        Refresh();
        OnPropertyChanged(nameof(CanToggleExclusion));
        OnPropertyChanged(nameof(ExclusionButtonLabel));
    }

    /// <summary>把当前来源加入 / 移出「不记录」名单；只影响之后的复制。</summary>
    public void ToggleSourceExclusion()
    {
        if (_exclusions is null || SelectedSource == SourceAll)
            return;

        var app = SelectedSource;
        var list = new List<string>(_exclusions.ExcludedApps);

        if (list.Contains(app, StringComparer.OrdinalIgnoreCase))
        {
            list.Remove(app);
            _exclusions.SaveExcludedApps(list);
            StatusText = $"已恢复记录 {app}";
        }
        else
        {
            list.Add(app);
            _exclusions.SaveExcludedApps(list);
            StatusText = $"已排除 {app}：之后从它复制的内容不再记录";
        }

        OnPropertyChanged(nameof(CanToggleExclusion));
        OnPropertyChanged(nameof(ExclusionButtonLabel));
    }

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
            SyncSourceFilters();

            var entries = string.IsNullOrWhiteSpace(Query)
                ? _store.Recent(MaxLoad)
                : _store.Search(Query, MaxLoad);

            var filtered = entries.Where(Matches).OrderByDescending(entry => entry.Favorite);
            var previousId = SelectedItem?.Id;

            Items.Clear();
            foreach (var entry in filtered)
                Items.Add(new ClipboardEntryViewModel(entry, _store));

            HasItems = Items.Count > 0;
            var megabytes = _store.ApproximateBytes / (1024.0 * 1024);
            Summary = Query.Length > 0
                ? $"匹配 {Items.Count} 条 · {megabytes:F1} MB"
                : $"共 {_store.Count} 条 · {megabytes:F1} MB · 显示 {Items.Count} 条";

            SyncTagFilters();

            SelectedItem = Items.FirstOrDefault(item => item.Id == previousId)
                ?? Items.FirstOrDefault();

            SyncQueueBadges();
            BuildGroups();
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

        var queued = Queue.FirstOrDefault(entry => entry.Entry.Id == item.Id);
        if (queued is not null)
        {
            Queue.Remove(queued);
            RenumberQueue();
        }

        // 先把图片字节留一份，删除会同时移除磁盘文件
        var png = item.Entry.Kind == ClipboardEntryKind.Image ? _store.LoadImage(item.Entry) : null;

        if (!_store.Delete(item.Id))
            return;

        _deleted.Add((item.Entry, png));
        if (_deleted.Count > 20)
            _deleted.RemoveAt(0);

        OnPropertyChanged(nameof(CanUndoDelete));
    }

    /// <summary>撤销最近一次删除；保留原来的收藏、标签和来源。</summary>
    public void UndoDelete()
    {
        if (_deleted.Count == 0)
            return;

        var (entry, png) = _deleted[^1];
        _deleted.RemoveAt(_deleted.Count - 1);

        _store.Restore(entry, png);
        OnPropertyChanged(nameof(CanUndoDelete));
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

    /// <summary>加入 / 移出顺序粘贴队列。</summary>
    public void ToggleQueue(ClipboardEntryViewModel item)
    {
        var index = Queue.ToList().FindIndex(queued => queued.Entry.Id == item.Id);

        if (index >= 0)
            Queue.RemoveAt(index);
        else
            Queue.Add(new ClipboardQueueItemViewModel(item.Entry, Queue.Count + 1));

        RenumberQueue();
    }

    public void RemoveQueueItem(ClipboardQueueItemViewModel item)
    {
        Queue.Remove(item);
        RenumberQueue();
    }

    public void ClearQueue()
    {
        Queue.Clear();
        RenumberQueue();
    }

    /// <summary>
    /// 取出队首并转成可粘贴内容；不可粘贴（超长文本、图片丢失）的条目跳过。
    /// 队列在取出时前进，不依赖写入剪贴板是否成功。
    /// </summary>
    public ClipboardPastePayload? TakeNextPaste()
    {
        while (Queue.Count > 0)
        {
            var entry = Queue[0].Entry;
            Queue.RemoveAt(0);
            RenumberQueue();

            switch (entry.Kind)
            {
                case ClipboardEntryKind.Text when entry.Text is { Length: > 0 }:
                    return new ClipboardPastePayload(entry.Kind, entry.Text, null, []);

                case ClipboardEntryKind.Image when _store.LoadImage(entry) is { Length: > 0 } png:
                    return new ClipboardPastePayload(entry.Kind, null, png, []);

                case ClipboardEntryKind.Files when entry.Paths.Count > 0:
                    return new ClipboardPastePayload(entry.Kind, null, null, entry.Paths);
            }
        }

        return null;
    }

    /// <summary>
    /// 粘贴下一条。simulate=true 走全局热键（写剪贴板 + 模拟 Ctrl+V）；
    /// false 只复制，供页面按钮使用。
    /// </summary>
    public bool PasteNext(bool simulate)
    {
        var payload = TakeNextPaste();
        return payload is not null && (_pasteService?.Paste(payload, simulate) ?? false);
    }

    /// <summary>拖出用：把条目转成临时文件所需的内容。</summary>
    public ClipboardPastePayload? GetDragPayload(ClipboardEntryViewModel item) => item.Entry.Kind switch
    {
        ClipboardEntryKind.Text when item.Entry.Text is { Length: > 0 } =>
            new ClipboardPastePayload(item.Entry.Kind, item.Entry.Text, null, []),

        ClipboardEntryKind.Image when _store.LoadImage(item.Entry) is { Length: > 0 } png =>
            new ClipboardPastePayload(item.Entry.Kind, null, png, []),

        ClipboardEntryKind.Files when item.Entry.Paths.Count > 0 =>
            new ClipboardPastePayload(item.Entry.Kind, null, null, item.Entry.Paths),

        _ => null
    };

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
        ClearQueue();
        _deleted.Clear();
        OnPropertyChanged(nameof(CanUndoDelete));
        _store.Clear();
    }

    private void SyncSourceFilters()
    {
        if (SourceFilters.Count == 0)
            SourceFilters.Add(SourceAll);

        var apps = _store.AllSourceApps();
        var current = SourceFilters.Skip(1).ToArray();

        if (!current.SequenceEqual(apps, StringComparer.OrdinalIgnoreCase))
        {
            SourceFilters.Clear();
            SourceFilters.Add(SourceAll);

            foreach (var app in apps)
                SourceFilters.Add(app);
        }

        if (SelectedSource != SourceAll &&
            !apps.Contains(SelectedSource, StringComparer.OrdinalIgnoreCase))
        {
            SelectedSource = SourceAll;
        }
    }

    /// <summary>收藏固定在最前，其余按时间分叠；空组不显示。</summary>
    private void BuildGroups()
    {
        Groups.Clear();

        var favorites = Items.Where(item => item.IsFavorite).ToList();
        if (favorites.Count > 0)
            Groups.Add(new ClipboardGroupViewModel(ClipboardTimeGroup.Favorites, favorites));

        var today = new List<ClipboardEntryViewModel>();
        var threeDays = new List<ClipboardEntryViewModel>();
        var week = new List<ClipboardEntryViewModel>();
        var older = new List<ClipboardEntryViewModel>();
        var now = DateTimeOffset.Now;

        foreach (var item in Items.Where(item => !item.IsFavorite))
        {
            switch (ClipboardTimeGroup.Resolve(item.Entry.CreatedAt, now))
            {
                case ClipboardTimeGroup.Today:
                    today.Add(item);
                    break;
                case ClipboardTimeGroup.ThreeDays:
                    threeDays.Add(item);
                    break;
                case ClipboardTimeGroup.Week:
                    week.Add(item);
                    break;
                default:
                    older.Add(item);
                    break;
            }
        }

        AddGroup(ClipboardTimeGroup.Today, today);
        AddGroup(ClipboardTimeGroup.ThreeDays, threeDays);
        AddGroup(ClipboardTimeGroup.Week, week);
        AddGroup(ClipboardTimeGroup.Older, older);

        void AddGroup(string title, List<ClipboardEntryViewModel> items)
        {
            if (items.Count > 0)
                Groups.Add(new ClipboardGroupViewModel(title, items));
        }
    }

    private void RenumberQueue()
    {
        var entries = Queue.Select(queued => queued.Entry).ToList();

        Queue.Clear();
        for (var index = 0; index < entries.Count; index++)
            Queue.Add(new ClipboardQueueItemViewModel(entries[index], index + 1));

        SyncQueueBadges();
        OnPropertyChanged(nameof(HasQueue));
        OnPropertyChanged(nameof(QueueHint));
    }

    private void SyncQueueBadges()
    {
        var numbers = Queue.ToDictionary(queued => queued.Entry.Id, queued => queued.Number);

        foreach (var item in Items)
        {
            item.IsQueued = numbers.TryGetValue(item.Id, out var number);
            item.QueueBadge = item.IsQueued ? number.ToString() : "＋";
        }
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

        if (SelectedSource != SourceAll &&
            !string.Equals(entry.SourceApp, SelectedSource, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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
