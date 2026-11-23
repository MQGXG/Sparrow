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
    public void 跨出屏幕边界_按交集拼接并保留黑色空隙()
    {
        if (!WgcCapture.IsSupported)
            return;

        // 请求区域左侧超出主屏 100px：只应抓到主屏那 100px，其余留黑
        var pixels = WgcCapture.CaptureRegion(-100, 0, 200, 100);

        if (pixels is null)
            Assert.Skip("当前会话无法捕获（锁屏 / 无交互桌面）");

        pixels!.Length.ShouldBe(200 * 100 * 4);

        // 最左侧 100 列不在任何显示器里
        var leftIsBlack = true;

        for (var y = 0; y < 100 && leftIsBlack; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                var index = (y * 200 + x) * 4;
                if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0)
                {
                    leftIsBlack = false;
                    break;
                }
            }
        }

        leftIsBlack.ShouldBeTrue();
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
