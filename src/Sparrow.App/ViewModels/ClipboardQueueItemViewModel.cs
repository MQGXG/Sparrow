using Sparrow.Plugin.Sdk;

namespace Sparrow.App.ViewModels;

/// <summary>顺序粘贴队列里的一项：序号 + 条目快照。</summary>
public sealed class ClipboardQueueItemViewModel
{
    public ClipboardQueueItemViewModel(ClipboardHistoryEntry entry, int number)
    {
        Entry = entry;
        Number = number;
    }

    public ClipboardHistoryEntry Entry { get; }

    public int Number { get; }

    public string Label => $"{Number}. {Shorten(Entry.Preview)}";

    private static string Shorten(string text) =>
        text.Length <= 28 ? text : text[..28] + "…";
}
