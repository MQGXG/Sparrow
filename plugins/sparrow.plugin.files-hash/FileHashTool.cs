using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.Plugin.Files.Hash;

/// <summary>
/// 文件哈希：一次算多个文件，结果是一张「文件 / 大小 / 哈希」的表。
///
/// 文件读取走宿主的 <see cref="IFileAccess"/>：只能读本次输入里出现的路径。
/// 用流而不是整块读入内存，几百 MB 的文件也不会把进程撑爆。
/// </summary>
public sealed class FileHashTool : ITool
{
    private const string ParameterAlgorithm = "algorithm";

    private const string AlgorithmSha256 = "SHA256";
    private const string AlgorithmSha1 = "SHA1";
    private const string AlgorithmMd5 = "MD5";
    private const string AlgorithmSha512 = "SHA512";

    /// <summary>读不到时哈希列里的固定文案；批量里其它文件继续算。</summary>
    private const string Unreadable = "无法读取";

    public ToolDescriptor Descriptor { get; } = new()
    {
        Id = "sparrow.files.hash",
        Name = "文件哈希",
        Category = "files",
        Description = "计算文件的 MD5 / SHA1 / SHA256 / SHA512",
        Keywords = ["hash", "哈希", "校验", "md5", "sha1", "sha256", "文件"],
        Accepts = InputShape.Files,
        Produces = OutputShape.Table,
        Order = 40,
        Parameters =
        [
            new ToolParameter
            {
                Key = ParameterAlgorithm,
                Label = "算法",
                Kind = ParameterKind.Choice,
                Choices = [AlgorithmSha256, AlgorithmMd5, AlgorithmSha1, AlgorithmSha512],
                Default = AlgorithmSha256,
                Hint = "SHA256 最常用；MD5 只用于和旧系统核对，不适合安全场景"
            }
        ]
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Input is not FileSetInput files)
            return ToolResult.InvalidInput("需要文件输入");

        if (files.Paths.Count == 0)
            return ToolResult.InvalidInput("没有可计算的文件");

        if (context.Files is null)
            return ToolResult.Failed("宿主未提供文件读取能力");

        var requested = context.GetParameter(ParameterAlgorithm, AlgorithmSha256);
        var algorithm = ResolveAlgorithm(requested);
        var label = algorithm.Name ?? AlgorithmSha256;

        var rows = new List<IReadOnlyList<string>>();

        try
        {
            for (var index = 0; index < files.Paths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = files.Paths[index];
                var name = Path.GetFileName(path);

                context.Progress?.Report(new ToolProgress(
                    index / (double)files.Paths.Count,
                    $"正在计算 {name}"));

                var row = await HashOneAsync(context, path, name, algorithm, cancellationToken);

                if (row is not null)
                    rows.Add(row);
            }
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Cancelled();
        }
        catch (Exception ex)
        {
            // 插件内部异常不能带回宿主
            context.Host?.Logger.LogError(ex, "文件哈希执行失败");
            return ToolResult.Failed("文件哈希失败", ex.Message);
        }

        if (rows.Count == 0 || rows.All(row => row[2] == Unreadable))
            return ToolResult.Failed("没有可读取的文件", "文件被占用、权限不足或已被移动");

        context.Progress?.Report(new ToolProgress(1, "整理结果"));

        var table = new TablePayload(["文件", "大小", "哈希（" + label + "）"], rows);
        return ToolResult.Ok(table);
    }

    /// <summary>
    /// 算一个文件；读不到时返回一行「无法读取」，让整批继续。
    /// 单独抽出来是为了让「单文件失败不中断」这条行为一眼可见。
    /// </summary>
    private static async Task<IReadOnlyList<string>?> HashOneAsync(
        ToolContext context,
        string path,
        string name,
        HashAlgorithmName algorithm,
        CancellationToken cancellationToken)
    {
        var size = TryGetSize(path);
        var stream = context.Files!.OpenRead(path);

        if (stream is null)
            return [name, size, Unreadable];

        try
        {
            var hash = await Task.Run(
                () => ComputeHash(stream, algorithm, cancellationToken),
                cancellationToken);

            return [name, size, hash];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [name, size, Unreadable];
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private static string ComputeHash(
        Stream stream,
        HashAlgorithmName algorithm,
        CancellationToken cancellationToken)
    {
        using var hasher = IncrementalHash.CreateHash(algorithm);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int read;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasher.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>未知算法回退 SHA256；不把参数错误变成执行失败。</summary>
    private static HashAlgorithmName ResolveAlgorithm(string requested) => requested switch
    {
        AlgorithmMd5 => HashAlgorithmName.MD5,
        AlgorithmSha1 => HashAlgorithmName.SHA1,
        AlgorithmSha512 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256
    };

    private static string TryGetSize(string path)
    {
        try
        {
            return FormatSize(new FileInfo(path).Length);
        }
        catch (Exception)
        {
            return "—";
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
