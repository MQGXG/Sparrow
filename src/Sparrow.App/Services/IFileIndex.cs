namespace Sparrow.App.Services;

/// <summary>
/// 文件索取的统一接口：简单枚举索引和 MFT 索引都实现它。
/// 窗口和 ViewModel 只依赖这个接口，换实现时不动物交互层。
/// </summary>
public interface IFileIndex : IDisposable
{
    /// <summary>索引是否已经可用。</summary>
    bool IsReady { get; }

    /// <summary>是否正在建立/加载索引。</summary>
    bool IsIndexing { get; }

    /// <summary>当前索引里的条目数。</summary>
    int Count { get; }

    /// <summary>可选的详细状态（例如失败原因）；没有时为 null。</summary>
    string? StatusMessage => null;

    /// <summary>索引内容变化时触发；UI 用它刷新结果。</summary>
    event EventHandler? Changed;

    /// <summary>开始建立或加载索引；重复调用是空操作。</summary>
    void Start();

    /// <summary>清空现有索引并重新建立。</summary>
    void Rebuild();

    /// <summary>按文件名查询；空查询返回空列表。</summary>
    IReadOnlyList<FileSearchItem> Search(string? query, int limit);
}
