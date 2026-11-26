namespace Sparrow.App.Services;

/// <summary>
/// 滚动截图的实时控制：UI 改开关，捕获循环每轮读取。
/// 用 Volatile 读写，避免后台线程看到半更新状态（.NET 的 bool/int 读写本身原子，
/// 但需要内存屏障才能保证 UI 线程的修改及时可见）。
/// </summary>
internal sealed class ScrollCaptureControl
{
    private int _autoScroll;
    private int _horizontal;
    private int _intervalMs = 240;

    /// <summary>是否由工具自动发送滚轮事件（关掉就是用户手动滚）。</summary>
    public bool AutoScroll
    {
        get => Volatile.Read(ref _autoScroll) != 0;
        set => Volatile.Write(ref _autoScroll, value ? 1 : 0);
    }

    /// <summary>横向滚动（代码、表格）；默认纵向。</summary>
    public bool Horizontal
    {
        get => Volatile.Read(ref _horizontal) != 0;
        set => Volatile.Write(ref _horizontal, value ? 1 : 0);
    }

    /// <summary>自动滚动时的节奏：每滚一次等多久再抓帧。</summary>
    public int IntervalMs
    {
        get => Volatile.Read(ref _intervalMs);
        set => Volatile.Write(ref _intervalMs, Math.Clamp(value, 60, 1200));
    }
}
