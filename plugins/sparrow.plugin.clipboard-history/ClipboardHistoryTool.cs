using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Clipboard.History;

/// <summary>
/// 剪贴板历史工具。
///
/// 监听与落盘都是宿主能力（IToolHost.ClipboardHistory），本工具只读：
/// 「浏览」把最近记录列成带序号的清单；「还原」按序号把内容重新变回结果载荷，
/// 复制动作交给宿主——工具不直接写剪贴板。
/// </summary>
public sealed class ClipboardHistoryTool : ITool
{
    private const string ParameterAction = "action";
    private const string ParameterKeyword = "keyword";
    private const string ParameterCount = "count";
    private const string ParameterIndex = "index";

    private const string ActionBrowse = "浏览";
    private const string ActionRestore = "还原";

    private const string KindText = "文本";
    private const string KindImage = "图片";
    private const string KindFiles = "文件";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.clipboard.history",
        Name = "剪贴板历史",
        Category = "clipboard",
        Description = "浏览最近复制过的文本 / 图片 / 文件，按序号还原",
        Keywords = ["剪贴板", "历史", "复制记录", "粘贴", "clipboard", "history"],
        Accepts = InputShape.None,
        Produces = OutputShape.Text,
        Requires = PlatformCapability.ClipboardMonitor,
        Order = 10,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterAction,
                Label = "动作",
                Kind = ParameterKind.Choice,
                Choices = [ActionBrowse, ActionRestore],
                Default = ActionBrowse
            },
            new ToolParameter
            {
                Key = ParameterKeyword,
                Label = "搜索关键字",
                Kind = ParameterKind.Text,
                Default = "",
                Hint = "仅「浏览」生效；按内容或文件路径过滤"
            },
            new ToolParameter
            {
                Key = ParameterCount,
                Label = "条数",
                Kind = ParameterKind.Number,
                Default = "20",
                Hint = "仅「浏览」生效"
            },
            new ToolParameter
            {
                Key = ParameterIndex,
                Label = "序号",
                Kind = ParameterKind.Number,
                Default = "1",
                Hint = "仅「还原」生效；序号来自浏览列表，1 是最新一条"
            }
        ]
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Host?.ClipboardHistory is not { } history)
        {
            return Task.FromResult(ToolResult.Failed(
                "剪贴板历史未开启",
                "宿主未注入 IClipboardHistory：检查设置 › 通用 › 记录剪贴板历史"));
        }

        var action = context.GetParameter(ParameterAction, ActionBrowse);
        return Task.FromResult(action == ActionRestore
            ? Restore(context, history)
            : Browse(context, history));
    }

    /// <summary>浏览和还原共用同一个视图：序号永远指向用户看到的同一份列表。</summary>
    private static IReadOnlyList<ClipboardHistoryEntry> GetView(
        ToolContext context,
        IClipboardHistory history,
        out string keyword)
    {
        keyword = context.GetParameter(ParameterKeyword, string.Empty).Trim();
        var count = Math.Clamp(context.GetParameter(ParameterCount, 20), 1, 500);

        return keyword.Length == 0
            ? history.Recent(count)
            : history.Search(keyword, count);
    }

    // ---- 浏览 ----

    private static ToolResult Browse(ToolContext context, IClipboardHistory history)
    {
        var entries = GetView(context, history, out var keyword);

        if (entries.Count == 0)
        {
            var emptyMessage = history.Count == 0
                ? "剪贴板历史为空，先去复制点东西"
                : $"没有匹配「{keyword}」的记录";

            return ToolResult.Ok(new TextPayload(emptyMessage));
        }

        var lines = new List<string>
        {
            keyword.Length == 0
                ? $"剪贴板历史 · 共 {history.Count} 条，显示最近 {entries.Count} 条"
                : $"剪贴板历史 · 匹配「{keyword}」{entries.Count} 条"
        };

        foreach (var (entry, position) in entries.Select((entry, i) => (entry, i + 1)))
        {
            var line = entry.Kind switch
            {
                ClipboardEntryKind.Text => $"{position}  {KindText}  {SummarizeSize(entry)}  {entry.Preview}",
                ClipboardEntryKind.Image => $"{position}  {KindImage}  {SummarizeSize(entry)}",
                _ => $"{position}  {KindFiles}  {entry.Preview}"
            };

            lines.Add(line);
        }

        lines.Add(string.Empty);
        lines.Add("把「动作」切到「还原」，填序号即可取回内容");

        return ToolResult.Ok(new TextPayload(string.Join('\n', lines)));
    }

    // ---- 还原 ----

    private static ToolResult Restore(ToolContext context, IClipboardHistory history)
    {
        var entries = GetView(context, history, out _);

        var index = context.GetParameter(ParameterIndex, 1);
        if (index < 1 || index > entries.Count)
            return ToolResult.InvalidInput($"序号超出范围：1 到 {entries.Count}");

        var entry = entries[index - 1];

        return entry.Kind switch
        {
            ClipboardEntryKind.Text when entry.Text is not null =>
                ToolResult.Ok(new TextPayload(entry.Text)),

            // 超长文本只记了摘要，全文没有落盘
            ClipboardEntryKind.Text =>
                ToolResult.Failed("该条目内容过长，未保留全文"),

            ClipboardEntryKind.Image =>
                RestoreImage(history, entry),

            ClipboardEntryKind.Files when entry.Paths.Count > 0 =>
                ToolResult.Ok(new FileSetPayload(entry.Paths)),

            _ => ToolResult.Failed("该条目没有可还原的内容")
        };
    }

    private static ToolResult RestoreImage(IClipboardHistory history, ClipboardHistoryEntry entry)
    {
        var png = history.LoadImage(entry);
        return png is null
            ? ToolResult.Failed("图片文件已被清理或丢失")
            : ToolResult.Ok(new ImagePayload(png, ImageFormat.Png));
    }

    private static string SummarizeSize(ClipboardHistoryEntry entry)
    {
        var time = entry.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");

        return entry.Kind switch
        {
            ClipboardEntryKind.Text => $"{time}  {entry.ByteSize} 字",
            ClipboardEntryKind.Image => $"{time}  {entry.ByteSize / 1024.0:F1} KB",
            _ => $"{time}  {entry.Paths.Count} 个"
        };
    }
}
