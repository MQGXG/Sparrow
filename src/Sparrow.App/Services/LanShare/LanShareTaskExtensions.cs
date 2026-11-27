namespace Sparrow.App.Services.LanShare;

/// <summary>
/// 让被放弃等待的任务继续保持异常被观察状态。
///
/// 来源：Lertaro.Core.Services.AbandonedTask（MIT）。搬迁后对外部类型不可见，
/// 这里复制一份到 LanShare 命名空间；语义与原实现一致。
/// </summary>
internal static class LanShareTaskExtensions
{
    internal static Task<T> KeepObserved<T>(this Task<T> pending)
    {
        _ = pending.ContinueWith(
            faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return pending;
    }
}
