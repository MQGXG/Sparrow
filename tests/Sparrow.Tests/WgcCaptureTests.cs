using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>WGC 真机探针：只在支持的系统上跑，验证能拿到 BGRA 帧。</summary>
public sealed class WgcCaptureTests
{
    [Fact]
    public void 抓取主屏左上角_返回BGRA像素()
    {
        if (!WgcCapture.IsSupported)
            return;

        var pixels = WgcCapture.CaptureRegion(0, 0, 200, 100);

        if (pixels is null)
            Assert.Skip("当前会话无法捕获（锁屏 / 无交互桌面）");

        pixels!.Length.ShouldBe(200 * 100 * 4);
        pixels!.Length.ShouldBe(200 * 100 * 4);
    }

    [Fact]
    public void 自动模式_取帧不因HDR检测或后端选择抛异常()
    {
        var previous = ScreenCapture.Api;

        try
        {
            ScreenCapture.Api = CaptureApi.Auto;

            var pixels = ScreenCapture.CaptureRaw(0, 0, 50, 50);

            if (pixels is null)
                Assert.Skip("当前会话无法捕获（锁屏 / 无交互桌面）");

            pixels!.Length.ShouldBe(50 * 50 * 4);
        }
        finally
        {
            ScreenCapture.Api = previous;
        }
    }

    [Fact]
    public void 开启光标_仍能取到像素()
    {
        var previousApi = ScreenCapture.Api;
        var previousCursor = ScreenCapture.ShowCursor;

        try
        {
            ScreenCapture.Api = CaptureApi.Gdi;
            ScreenCapture.ShowCursor = true;

            var pixels = ScreenCapture.CaptureRaw(0, 0, 60, 60);

            if (pixels is null)
                Assert.Skip("当前会话无法捕获（锁屏 / 无交互桌面）");

            pixels!.Length.ShouldBe(60 * 60 * 4);
        }
        finally
        {
            ScreenCapture.Api = previousApi;
            ScreenCapture.ShowCursor = previousCursor;
        }
    }
}
