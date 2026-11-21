using System.Diagnostics;

namespace Sparrow.Plugin.Ocr;

/// <summary>
/// 打开一个 OCR worker 会话。
///
/// 批量识别必须复用同一个进程：模型初始化要 1-3 秒，每张图都重启 worker
/// 会让「批量」变成「一张图的时间 × 张数」。
/// 抽成接口是为了让批量逻辑能在测试里脱离真实模型运行。
/// </summary>
public interface IOcrWorkerRunner
{
    /// <param name="mode">worker 模式：<c>text</c> 文字识别 / <c>table</c> 表格结构。</param>
    Task<IOcrWorkerSession> StartAsync(
        string workerPath,
        string language,
        string mode,
        CancellationToken cancellationToken);
}

/// <summary>一个存活的 worker 进程；<see cref="RecognizeAsync"/> 可连续调用。</summary>
public interface IOcrWorkerSession : IAsyncDisposable
{
    /// <summary>识别一张图，返回 worker 的原始 JSON。失败抛 <see cref="OcrWorkerException"/>。</summary>
    Task<string> RecognizeAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken);
}

/// <summary>worker 对某一张图返回了错误帧（图片坏、模型内部异常等）。</summary>
public sealed class OcrWorkerException : Exception
{
    public OcrWorkerException(string message) : base(message)
    {
    }
}

/// <summary>
/// 真实的子进程实现。
///
/// 协议（宿主与 worker 的唯一契约）：
///   args   = --language &lt;code&gt;（可选，默认 ch）+ --mode text|table（可选，默认 text）
///   stdin  = 重复的 [4 字节小端长度][图片字节]
///   stdout = 每帧一行 JSON：{ text, regions[...] } / { html } 或 { error }
///   关闭 stdin 后进程退出；exit 0 = 正常
/// </summary>
public sealed class ProcessOcrWorkerRunner : IOcrWorkerRunner
{
    public Task<IOcrWorkerSession> StartAsync(
        string workerPath,
        string language,
        string mode,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--language");
        startInfo.ArgumentList.Add(language);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(mode);

        var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 OCR worker");

        // stderr 必须持续读走，否则管道写满会让 worker 卡死
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        return Task.FromResult<IOcrWorkerSession>(new ProcessOcrWorkerSession(process, errorTask));
    }
}

internal sealed class ProcessOcrWorkerSession : IOcrWorkerSession
{
    private static readonly TimeSpan RecognizeTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly Task<string> _errorTask;
    private bool _disposed;

    public ProcessOcrWorkerSession(Process process, Task<string> errorTask)
    {
        _process = process;
        _errorTask = errorTask;
    }

    public async Task<string> RecognizeAsync(
        ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RecognizeTimeout);

        try
        {
            var header = BitConverter.GetBytes(image.Length);
            await _process.StandardInput.BaseStream.WriteAsync(header, timeout.Token);
            await _process.StandardInput.BaseStream.WriteAsync(image, timeout.Token);
            await _process.StandardInput.BaseStream.FlushAsync(timeout.Token);

            // worker 每帧输出一行；JSON 是紧凑格式，不含换行。
            // 但 Paddle 原生层出错时会把 C++ 回溯直接写到 stdout（几十行），
            // 那不是协议的一部分，跳过直到拿到 JSON 帧或进程退出。
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    var error = _errorTask.IsCompletedSuccessfully ? _errorTask.Result : string.Empty;
                    throw new OcrWorkerException(
                        $"worker 提前退出（退出码 {SafeExitCode()}）：{error.Trim()}");
                }

                if (line.TrimStart().StartsWith('{'))
                    return line;
            }

            throw new OcrWorkerException("worker 输出里没有可解析的 JSON");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OcrWorkerException("OCR 超时（单张 30 秒）");
        }
    }

    private int? SafeExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            // 关闭 stdin = 告诉 worker 没有更多图了
            _process.StandardInput.Close();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception)
        {
            // 退出慢或已被杀都不影响结果
        }

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程已退出
            }
        }

        _process.Dispose();
    }
}
