using Shouldly;
using Sparrow.Plugin.Ocr;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

public sealed class WarmWorkerPoolTests
{
    private sealed class FakeSession : IOcrWorkerSession
    {
        public int DisposeCount { get; private set; }

        public bool Throw { get; set; }

        public Task<string> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken) =>
            Throw
                ? Task.FromException<string>(new InvalidOperationException("worker 坏了"))
                : Task.FromResult("{}");

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRunner : IOcrWorkerRunner
    {
        public List<FakeSession> Created { get; } = [];

        public int Sessions => Created.Count;

        public Task<IOcrWorkerSession> StartAsync(
            string workerPath,
            string language,
            string mode,
            CancellationToken cancellationToken)
        {
            var session = new FakeSession();
            Created.Add(session);
            return Task.FromResult<IOcrWorkerSession>(session);
        }
    }

    [Fact]
    public async Task 同一语言复用会话_关闭池时才销毁()
    {
        var runner = new FakeRunner();
        await using var pool = new WarmWorkerPool(runner);

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: true, TestContext.Current.CancellationToken))
        {
        }

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: true, TestContext.Current.CancellationToken))
        {
        }

        runner.Sessions.ShouldBe(1);
        runner.Created[0].DisposeCount.ShouldBe(0);

        await pool.DisposeAsync();

        runner.Created[0].DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task 换语言会销毁旧会话并起新的()
    {
        var runner = new FakeRunner();
        await using var pool = new WarmWorkerPool(runner);

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: true, TestContext.Current.CancellationToken))
        {
        }

        await using (await pool.AcquireAsync("w", "en", "text", keepWarm: true, TestContext.Current.CancellationToken))
        {
        }

        runner.Sessions.ShouldBe(2);
        runner.Created[0].DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task 空闲超时后自动销毁()
    {
        var runner = new FakeRunner();
        await using var pool = new WarmWorkerPool(runner, TimeSpan.FromMilliseconds(30));

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: true, TestContext.Current.CancellationToken))
        {
        }

        // 并行测试负载下 30ms 空闲计时器会被线程池延后，轮询而不是死等固定时长。
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (runner.Created[0].DisposeCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        runner.Created[0].DisposeCount.ShouldBe(1);

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: true, TestContext.Current.CancellationToken))
        {
        }

        runner.Sessions.ShouldBe(2);
    }

    [Fact]
    public async Task 关闭复用时每次用完立即销毁()
    {
        var runner = new FakeRunner();
        await using var pool = new WarmWorkerPool(runner);

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: false, TestContext.Current.CancellationToken))
        {
        }

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: false, TestContext.Current.CancellationToken))
        {
        }

        runner.Sessions.ShouldBe(2);
        runner.Created.ShouldAllBe(session => session.DisposeCount == 1);
    }

    [Fact]
    public async Task 切换到关闭复用时立即销毁已保留的会话()
    {
        var runner = new FakeRunner();
        await using var pool = new WarmWorkerPool(runner);

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: true, TestContext.Current.CancellationToken))
        {
        }

        runner.Created[0].DisposeCount.ShouldBe(0);

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: false, TestContext.Current.CancellationToken))
        {
        }

        runner.Created[0].DisposeCount.ShouldBe(1);
        runner.Sessions.ShouldBe(2);
    }

    [Fact]
    public async Task 会话抛异常后不再复用()
    {
        var runner = new FakeRunner();
        await using var pool = new WarmWorkerPool(runner);

        var first = await pool.AcquireAsync("w", "ch", "text", keepWarm: true, TestContext.Current.CancellationToken);
        runner.Created[0].Throw = true;

        await Should.ThrowAsync<InvalidOperationException>(
            () => first.RecognizeAsync(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
        await first.DisposeAsync();

        runner.Created[0].DisposeCount.ShouldBe(1);

        await using (await pool.AcquireAsync("w", "ch", "text", keepWarm: true, TestContext.Current.CancellationToken))
        {
        }

        runner.Sessions.ShouldBe(2);
    }
}
