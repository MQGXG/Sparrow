using Sparrow.App.History;

namespace Sparrow.Tests;

/// <summary>测试用的空历史存储。</summary>
internal sealed class NullHistoryStore : IHistoryStore
{
    public void Add(HistoryEntry entry)
    {
    }

    public IReadOnlyList<HistoryEntry> Recent(int count) => [];

    public void Clear()
    {
    }
}
