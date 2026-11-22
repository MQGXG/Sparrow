namespace Sparrow.Plugin.Ocr;

/// <summary>
/// OCR worker 热池：一次识别结束后把进程停一会儿，下一次同语言/同模式的识别直接复用，
/// 省掉 1–3 秒的模型初始化。空闲超过 <see cref="DefaultIdleTimeout"/> 自动退出。
///
/// 只保留一个热会话：不同语言/模式会替换旧的，内存上界等于一个 worker。
/// 用户可在 OCR 参数里把「复用 worker」关掉，退回每次用完立即退出。
///
/// 池在插件工具实例里，工具实例随插件加载常驻，所以热会话能跨多次截图/粘贴存活；
/// 应用退出时 stdin 管道关闭，worker 自己会退出，不依赖定时器。
/// </summary>
internal sealed class WarmWorkerPool : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly IOcrWorkerRunner _runner;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IOcrWorkerSession? _warm;
    private string? _warmKey;
    private Timer? _idleTimer;
    private bool _disposed;

    public WarmWorkerPool(IOcrWorkerRunner runner, TimeSpan? idleTimeout = null)
    {
        _runner = runner;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
    }

    /// <summary>
    /// 取一个会话。返回的 lease 必须 <c>await using</c>：
    /// 正常结束会归还热池，异常/取消/关闭复用时直接销毁。
    /// </summary>
    public async Task<IOcrWorkerSession> AcquireAsync(
        string workerPath,
        string language,
        string mode,
        bool keepWarm,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = $"{workerPath}|{language}|{mode}";

        if (!keepWarm)
        {
            // 用户把复用关掉：顺手收掉上一次留在池里的 worker，不等到超时
            await KillIdleAsync();
            return new Lease(this, await _runner.StartAsync(workerPath, language, mode, cancellationToken), key, parkable: false);
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_warm is not null)
            {
                var warm = _warm;
                var warmKey = _warmKey;
                _warm = null;
                _warmKey = null;
                StopIdleTimer();

                if (string.Equals(warmKey, key, StringComparison.Ordinal) && IsUsable(warm))
                    return new Lease(this, warm, key, parkable: true);

                await warm.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }

        var session = await _runner.StartAsync(workerPath, language, mode, cancellationToken);
        return new Lease(this, session, key, parkable: true);
    }

    private static bool IsUsable(IOcrWorkerSession session) =>
        session is not ProcessOcrWorkerSession process || process.IsAlive;

    private async ValueTask<bool> TryParkAsync(IOcrWorkerSession session, string key)
    {
        await _gate.WaitAsync();

        try
        {
            if (_disposed || _warm is not null)
                return false;

            _warm = session;
            _warmKey = key;
            ArmIdleTimer();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ArmIdleTimer()
    {
        StopIdleTimer();
        _idleTimer = new Timer(
            static state => _ = ((WarmWorkerPool)state!).KillIdleAsync(),
            this,
            _idleTimeout,
            Timeout.InfiniteTimeSpan);
    }

    private void StopIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private async Task KillIdleAsync()
    {
        // 池正被占用（有调用在取/还）就等下一轮
        if (!await _gate.WaitAsync(0))
            return;

        try
        {
            if (_warm is null)
                return;

            var warm = _warm;
            _warm = null;
            _warmKey = null;
            StopIdleTimer();

            await warm.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        StopIdleTimer();

        await _gate.WaitAsync();

        try
        {
            if (_warm is null)
                return;

            var warm = _warm;
            _warm = null;
            _warmKey = null;

            await warm.DisposeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Lease : IOcrWorkerSession
    {
        private readonly WarmWorkerPool _pool;
        private readonly IOcrWorkerSession _inner;
        private readonly string _key;
        private readonly bool _parkable;

        private bool _released;
        private bool _broken;

        public Lease(WarmWorkerPool pool, IOcrWorkerSession inner, string key, bool parkable)
        {
            _pool = pool;
            _inner = inner;
            _key = key;
            _parkable = parkable;
        }

        public async Task<string> RecognizeAsync(
            ReadOnlyMemory<byte> image,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _inner.RecognizeAsync(image, cancellationToken);
            }
            catch
            {
                // 通信异常/超时/取消：进程状态不再可信，下一次重新起
                _broken = true;
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_released)
                return;

            _released = true;

            if (_parkable
                && !_broken
                && IsUsable(_inner)
                && await _pool.TryParkAsync(_inner, _key))
            {
                return;
            }

            await _inner.DisposeAsync();
        }
    }
}
