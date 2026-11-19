using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 剪贴板页里的一行。
/// 缩略图是懒加载：只有真正渲染到列表时才去读 PNG，避免一开页把几十张图全读进内存。
/// </summary>
public sealed partial class ClipboardEntryViewModel : ObservableObject
{
    private readonly Lazy<byte[]?> _thumbnail;

    public ClipboardEntryViewModel(ClipboardHistoryEntry entry, ClipboardHistoryStore store)
    {
        Entry = entry;
        _thumbnail = new Lazy<byte[]?>(() =>
            entry.Kind == ClipboardEntryKind.Image ? store.LoadImage(entry) : null);
    }

    public ClipboardHistoryEntry Entry { get; }

    /// <summary>是否是右侧详情正在展示的那一条。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>是否在顺序粘贴队列里。</summary>
    [ObservableProperty]
    private bool _isQueued;

    /// <summary>行内队列按钮上的标记：未入队为「＋」，入队后显示序号。</summary>
    [ObservableProperty]
    private string _queueBadge = "＋";

    public string Id => Entry.Id;

    public bool IsFavorite => Entry.Favorite;

    /// <summary>手动标签；详情里可逐条移除。</summary>
    public IReadOnlyList<string> Tags => Entry.Tags;

    public string Preview => Entry.Preview;

    public string KindLabel => Entry.Kind switch
    {
        ClipboardEntryKind.Text => "文本",
        ClipboardEntryKind.Image => "图片",
        _ => "文件"
    };

    /// <summary>缩略图占位字符：没有缩略图时给一个一眼能分辨的标记。</summary>
    public string KindGlyph => Entry.Kind switch
    {
        ClipboardEntryKind.Text => "文",
        ClipboardEntryKind.Image => "图",
        _ => "件"
    };

    public bool HasThumbnail => Entry.Kind == ClipboardEntryKind.Image;

    public byte[]? Thumbnail => _thumbnail.Value;

    public bool HasTags => TagLine.Length > 0;

    /// <summary>图片条目已有「图片」类型，不再重复显示自动标签行。</summary>
    public bool ShowTagLine => HasTags && !HasThumbnail;

    public string TagLine => string.Join("  ",
        Entry.Tags.Concat(Entry.AutoTags).Distinct(StringComparer.OrdinalIgnoreCase));

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    /// <summary>来源进程名；拿不到时为空串（界面隐藏）。</summary>
    public string SourceLabel => Entry.SourceApp ?? string.Empty;

    public bool HasSource => SourceLabel.Length > 0;

    public string TimeLabel => Entry.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");

    /// <summary>详情里的完整时间：列表只给到分钟，精确定位要秒。</summary>
    public string DetailTimeLabel => Entry.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>详情里的来源值；没有来源时给一个占位符，避免那一栏留空。</summary>
    public string SourceDisplay => SourceLabel.Length > 0 ? SourceLabel : "—";

    /// <summary>是否有手动标签（自动标签不算），详情底栏靠它显示空态提示。</summary>
    public bool HasManualTags => Entry.Tags.Count > 0;

    public string SizeLabel => Entry.Kind switch
    {
        ClipboardEntryKind.Text => $"{Entry.ByteSize} 字",
        ClipboardEntryKind.Image => $"{Entry.ByteSize / 1024.0:F1} KB",
        _ => $"{Entry.Paths.Count} 个"
    };
}
