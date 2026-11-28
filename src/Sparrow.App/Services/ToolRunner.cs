using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 工具执行的唯一入口。
///
/// 两条约束：
/// 1. <c>ITool.ExecuteAsync</c> 既可能是真异步（OCR / 翻译），也可能是
///    <c>Task.FromResult</c> 包着的同步计算（JSON / Base64 / 图片转换）。
///    统一先用 <see cref="Task.Run(Func{Task}, CancellationToken)"/> 放到线程池，
///    避免同步插件在 UI 线程上直接执行把界面冻住。
/// 2. 插件忽略取消令牌时，宿主不能无限等：给一个宽松的兜底超时，
///    超时后立即回到调用方（后台工作无法强制终止，只能等它自己结束）。
///
/// 超时值刻意取得宽松（10 分钟）：本地翻译在 CPU 上可能跑很久，
/// 这里只防「插件挂死」，不限制正常的长任务。
/// </summary>
public static class ToolRunner
{
    /// <summary>兜底超时。正常工具远小于这个值。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 重任务闸门：同一时间只跑一个 <see cref="ToolDescriptor.IsHeavy"/> 工具。
    /// 本地模型 / OCR / 视频转码会一次性吃掉大量内存或 CPU，
    /// 同时跑两个的代价远大于排队等待。
    ///
    /// 注意：插件忽略取消令牌时，闸门会一直等到它真正结束——
    /// 进程内无法强制终止第三方代码，这是已知边界。
    /// </summary>
    private static readonly SemaphoreSlim HeavyGate = new(1, 1);

    public static async Task<ToolResult> ExecuteAsync(
        ITool tool,
        ToolContext context,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);

        var limit = timeout ?? DefaultTimeout;

        using var timeoutSource = new CancellationTokenSource(limit);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var heavy = tool.Descriptor.IsHeavy;
        var gateHeld = false;

        if (heavy)
        {
            try
            {
                await HeavyGate.WaitAsync(linkedSource.Token);
                gateHeld = true;
            }
            catch (OperationCanceledException)
                when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return ToolResult.Timeout($"等待重任务让出执行槽位超时（超过 {limit.TotalSeconds:0} 秒）");
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Cancelled();
            }
        }

        // 关键：ExecuteAsync 的调用本身也放到线程池，同步插件不会占用 UI 线程
        var work = Task.Run(
            () => tool.ExecuteAsync(context, linkedSource.Token),
            CancellationToken.None);

        if (gateHeld)
        {
            // 闸门跟随「工作真正结束」释放，而不是超时返回时释放；
            // 被放弃的后台重任务仍然占着它的槽位。
            _ = work.ContinueWith(
                static (_, state) => ((SemaphoreSlim)state!).Release(),
                HeavyGate,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try
        {
            var result = await work.WaitAsync(linkedSource.Token);
            return ToolResultLimits.Apply(result);
        }
        catch (OperationCanceledException)
            when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Observe(work);
            return ToolResult.Timeout($"执行超时（超过 {limit.TotalSeconds:0} 秒）");
        }
        catch (OperationCanceledException)
        {
            Observe(work);
            return ToolResult.Cancelled();
        }
    }

    /// <summary>
    /// 超时/取消后后台工作可能还会结束；观察一次异常，
    /// 避免它变成未处理的任务异常。
    /// </summary>
    private static void Observe(Task work)
    {
        _ = work.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
