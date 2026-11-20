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
    private readonly ClipboardHistoryStore _store;
    private readonly Lazy<byte[]?> _thumbnail;
    private readonly Lazy<byte[]?> _sourceIcon;

    public ClipboardEntryViewModel(ClipboardHistoryEntry entry, ClipboardHistoryStore store)
    {
        Entry = entry;
        _store = store;
        LinkHost = ExtractHost(entry);
        _thumbnail = new Lazy<byte[]?>(() =>
            entry.Kind == ClipboardEntryKind.Image ? store.LoadImage(entry) : null);

        // 图标提取要枚举进程：懒加载 + 进程级缓存，列表滚动时每个来源只查一次
        _sourceIcon = new Lazy<byte[]?>(() => SourceIconProvider.TryGet(entry.SourceApp));
    }

    /// <summary>从 http(s) 链接里取域名（去掉 www.），失败返回空串。</summary>
    private static string ExtractHost(ClipboardHistoryEntry entry)
    {
        var firstLine = FirstLineOf(entry);
        if (firstLine.Length == 0)
            return string.Empty;

        if (!Uri.TryCreate(firstLine, UriKind.Absolute, out var uri))
            return string.Empty;

        if (uri.Scheme is not ("http" or "https"))
            return string.Empty;

        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    /// <summary>文本条目的第一行（去掉首尾空白）；非文本返回空串。</summary>
    private static string FirstLineOf(ClipboardHistoryEntry entry)
    {
        if (entry.Kind != ClipboardEntryKind.Text || entry.Text is not { Length: > 0 } text)
            return string.Empty;

        var firstLine = text.TrimStart();
        var end = firstLine.IndexOfAny(['\r', '\n']);
        return (end >= 0 ? firstLine[..end] : firstLine).Trim();
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

    public string KindLabel => IsLink
        ? "链接"
        : Entry.Kind switch
    {
        ClipboardEntryKind.Text => "文本",
        ClipboardEntryKind.Image => "图片",
        _ => "文件"
    };

    /// <summary>缩略图占位用的类型图标，与缩略图互斥；四者互斥，一次只有一个可见。</summary>
    public bool IsTextKind => Entry.Kind == ClipboardEntryKind.Text && !IsLink;

    public bool IsImageKind => Entry.Kind == ClipboardEntryKind.Image;

    public bool IsFileKind => Entry.Kind == ClipboardEntryKind.Files;

    public bool HasThumbnail => Entry.Kind == ClipboardEntryKind.Image;

    public byte[]? Thumbnail => _thumbnail.Value;

    public bool HasTags => TagLine.Length > 0;

    /// <summary>图片、链接、文件各有专用布局，不再重复显示自动标签。</summary>
    public bool ShowTagLine => HasTags && !HasThumbnail && !IsFileSet && !IsLink;

    /// <summary>链接条目：显示 favicon、域名和 URL 分层。</summary>
    public bool IsLink => LinkHost.Length > 0;

    /// <summary>链接域名，如 github.com。</summary>
    public string LinkHost { get; }

    /// <summary>favicon 占位字母（域名首字母大写）。</summary>
    public string LinkInitial => LinkHost.Length > 0
        ? LinkHost[..1].ToUpperInvariant()
        : "?";

    /// <summary>文件条目：显示文件名和数量。</summary>
    public bool IsFileSet => Entry.Kind == ClipboardEntryKind.Files;

    public string FileTitle => Entry.Paths.Count > 0
        ? System.IO.Path.GetFileName(Entry.Paths[0])
        : "文件";

    public string FileSubtitle => Entry.Paths.Count > 1
        ? $"+{Entry.Paths.Count - 1} 个文件"
        : string.Empty;

    public bool HasFileSubtitle => FileSubtitle.Length > 0;

    /// <summary>普通文本预览：图片、链接、文件各有专用布局。</summary>
    public bool ShowPlainPreview => !HasThumbnail && !IsLink && !IsFileSet;

    /// <summary>文本里存的是一个存在的本地路径（文件或目录）。</summary>
    public bool IsPath => Entry.Kind == ClipboardEntryKind.Text
        && !IsLink
        && IsExistingPath(FirstLineOf(Entry));

    private static bool IsExistingPath(string text) =>
        text.Length > 2
        && (File.Exists(text) || Directory.Exists(text));

    /// <summary>链接、存在的本地路径、可访问的文件、图片都可以用默认程序打开。</summary>
    public bool CanOpen => IsLink
        || IsPath
        || (Entry.Kind == ClipboardEntryKind.Files
            && Entry.Paths.Count > 0
            && File.Exists(Entry.Paths[0]))
        || Entry.Kind == ClipboardEntryKind.Image;

    /// <summary>
    /// 解析打开目标：链接 → URL；文本路径 → 路径；文件 → 现有路径；
    /// 图片 → 写入临时 PNG 后返回路径。无法打开时返回 null。
    /// </summary>
    public string? ResolveOpenTarget()
    {
        if (IsLink)
        {
            var firstLine = FirstLineOf(Entry);
            return Uri.TryCreate(firstLine, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                ? uri.AbsoluteUri
                : null;
        }

        if (Entry.Kind == ClipboardEntryKind.Text)
        {
            var line = FirstLineOf(Entry);
            return IsExistingPath(line) ? line : null;
        }

        if (Entry.Kind == ClipboardEntryKind.Files && Entry.Paths.Count > 0)
            return File.Exists(Entry.Paths[0]) ? Entry.Paths[0] : null;

        if (Entry.Kind == ClipboardEntryKind.Image)
        {
            var png = _store.LoadImage(Entry);
            if (png is not { Length: > 0 })
                return null;

            var directory = Path.Combine(Path.GetTempPath(), "Sparrow", "open");
            Directory.CreateDirectory(directory);
            CleanupOldOpenFiles(directory);

            var path = Path.Combine(directory, $"{Entry.Id}.png");
            File.WriteAllBytes(path, png);
            return path;
        }

        return null;
    }

    /// <summary>文件条目可以在资源管理器里定位。</summary>
    public bool CanReveal => Entry.Kind == ClipboardEntryKind.Files
        && Entry.Paths.Count > 0
        && File.Exists(Entry.Paths[0]);

    /// <summary>在资源管理器里选中文件（不是打开）。</summary>
    public void RevealInExplorer()
    {
        if (!CanReveal)
            return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "explorer.exe",
            $"/select,\"{Entry.Paths[0]}\"")
        {
            UseShellExecute = true
        });
    }

    /// <summary>用系统默认程序打开条目。</summary>
    public void Open()
    {
        var target = ResolveOpenTarget();
        if (target is null)
            return;

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
    }

    /// <summary>清掉一天前为「打开」写出的临时图片。</summary>
    private static void CleanupOldOpenFiles(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // 文件仍被看图程序占用：下次再清
                }
            }
        }
        catch
        {
            // 临时目录不可用不影响打开
        }
    }

    public string TagLine => string.Join("  ",
        Entry.Tags.Concat(Entry.AutoTags).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>来源进程名；拿不到时为空串（界面隐藏）。</summary>
    public string SourceLabel => Entry.SourceApp ?? string.Empty;

    public bool HasSource => SourceLabel.Length > 0;

    public string TimeLabel => Entry.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");

    /// <summary>详情里的完整时间：列表只给到分钟，精确定位要秒。</summary>
    public string DetailTimeLabel => Entry.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>详情里的来源值；没有来源时给一个占位符，避免那一栏留空。</summary>
    public string SourceDisplay => SourceLabel.Length > 0 ? SourceLabel : "—";

    /// <summary>来源应用图标（PNG）；来源进程已退出时为 null，界面回退到首字母色块。</summary>
    public byte[]? SourceIconPng => _sourceIcon.Value;

    public bool HasSourceIcon => SourceIconPng is not null;

    /// <summary>拿不到图标时的回退：进程名首字母。</summary>
    public string SourceInitial => SourceLabel.Length > 0
        ? SourceLabel[..1].ToUpperInvariant()
        : string.Empty;

    /// <summary>是否有手动标签（自动标签不算），详情底栏靠它显示空态提示。</summary>
    public bool HasManualTags => Entry.Tags.Count > 0;

    public string SizeLabel => Entry.Kind switch
    {
        ClipboardEntryKind.Text => $"{Entry.ByteSize} 字",
        ClipboardEntryKind.Image => $"{Entry.ByteSize / 1024.0:F1} KB",
        _ => $"{Entry.Paths.Count} 个"
    };
}
