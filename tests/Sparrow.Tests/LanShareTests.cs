using System.Net;
using System.Net.Sockets;
using Shouldly;
using Sparrow.App.Services.LanShare;
using Sparrow.App.Services.LanShare.Models;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 局域网互传的端到端冒烟：本机起服务，用同一套客户端发一个文件，
/// 验证 HTTPS + 客户端证书 + 免确认接收 + 落盘这条链路。
/// </summary>
public sealed class LanShareTests
{
    [Fact]
    public async Task 本机发送_文件落到接收目录()
    {
        var download = Path.Combine(
            Path.GetTempPath(),
            "sparrow-lanshare-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(
            Path.GetTempPath(),
            "sparrow-lanshare-send-" + Guid.NewGuid().ToString("N") + ".txt");

        Directory.CreateDirectory(download);
        await File.WriteAllTextAsync(source, "hello lanshare", TestContext.Current.CancellationToken);

        try
        {
            using var identity = LocalSendCertificate.LoadOrCreate();

            var port = GetFreePort();

            using var server = new LocalSendServer
            {
                Certificate = identity,
                IdentityCertificate = identity,
                DownloadDirectory = download,
                QuickSave = true,
                DeviceInfo = new LocalSendDeviceInfo
                {
                    Alias = "SparrowTest",
                    Port = port,
                    Protocol = "https"
                }
            };

            server.Start(port);

            using var client = new LocalSendClient(server);
            var sender = new LocalSendDeviceInfo { Alias = "SparrowTestSender" };

            var result = await client.SendFilesAsync(
                "127.0.0.1",
                server.ActualPort,
                https: true,
                sender,
                [source],
                token: TestContext.Current.CancellationToken);

            result.ShouldBe(LocalSendSendResult.Success);

            var received = Directory
                .GetFiles(download, "*.txt", SearchOption.AllDirectories)
                .ShouldHaveSingleItem();

            (await File.ReadAllTextAsync(received, TestContext.Current.CancellationToken))
                .ShouldBe("hello lanshare");
        }
        finally
        {
            TryDelete(source);
            TryDeleteDirectory(download);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
