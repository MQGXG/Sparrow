using Sparrow.App.History;

namespace Sparrow.App.ViewModels;

/// <summary>历史列表里的一行。</summary>
public sealed class HistoryEntryViewModel
{
    public HistoryEntryViewModel(HistoryEntry entry) => Entry = entry;

    public HistoryEntry Entry { get; }

    public string ToolName => Entry.ToolName;

    public string TimeLabel => Entry.CreatedAt.LocalDateTime.ToString("HH:mm:ss");

    public string Preview => Entry.Output.Length <= 80
        ? Entry.Output
        : Entry.Output[..80] + "…";
}
