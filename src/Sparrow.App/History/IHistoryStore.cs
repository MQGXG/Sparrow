namespace Sparrow.App.History;

/// <summary>
/// 执行历史。
///
/// M1 用 JSONL 落盘（零依赖、可手工查看），接口按 SQLite 的能力设计：
/// 以后换成 SQLite 时，调用方不用改。
/// </summary>
public interface IHistoryStore
{
    void Add(HistoryEntry entry);

    /// <summary>最近的记录，按时间倒序。</summary>
    IReadOnlyList<HistoryEntry> Recent(int count);

    void Clear();
}
