using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>截图 OCR 的识别框模式与冻结帧裁剪。</summary>
public sealed class CaptureOcrBoxModesTests
{
    [Theory]
    [InlineData("all", OcrBoxMode.All)]
    [InlineData("HOVER", OcrBoxMode.Hover)]
    [InlineData(" off ", OcrBoxMode.Off)]
    [InlineData("", OcrBoxMode.Hover)]
    [InlineData(null, OcrBoxMode.Hover)]
    [InlineData("unknown", OcrBoxMode.Hover)]
    public void 解析模式_未知值回退悬停(string? value, OcrBoxMode expected)
    {
        OcrBoxModes.Parse(value).ShouldBe(expected);
    }

    [Fact]
    public void 循环切换_全部到悬停到关闭()
    {
        OcrBoxModes.Next(OcrBoxMode.All).ShouldBe(OcrBoxMode.Hover);
        OcrBoxModes.Next(OcrBoxMode.Hover).ShouldBe(OcrBoxMode.Off);
        OcrBoxModes.Next(OcrBoxMode.Off).ShouldBe(OcrBoxMode.All);
    }

    [Fact]
    public void 格式化与标签()
    {
        OcrBoxModes.Format(OcrBoxMode.All).ShouldBe("all");
        OcrBoxModes.Format(OcrBoxMode.Off).ShouldBe("off");
        OcrBoxModes.Label(OcrBoxMode.Hover).ShouldBe("框 · 悬停");
    }

    [Fact]
    public void 冻结帧裁剪_按行拷贝像素()
    {
        // 4x3，每个像素 BGRA 编码为 (x, y, 0, 255)
        const int width = 4;
        const int height = 3;
        var source = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                source[offset] = (byte)x;
                source[offset + 1] = (byte)y;
                source[offset + 2] = 0;
                source[offset + 3] = 255;
            }
        }

        var cropped = ScreenCapture.Crop(source, width, height, 1, 1, 2, 2).ShouldNotBeNull();

        cropped.Length.ShouldBe(2 * 2 * 4);
        cropped[0].ShouldBe((byte)1);
        cropped[1].ShouldBe((byte)1);
        cropped[4].ShouldBe((byte)2); // 第二列
        cropped[8].ShouldBe((byte)1); // 第二行
        cropped[9].ShouldBe((byte)2);
    }

    [Fact]
    public void 冻结帧裁剪_越界返回空()
    {
        var source = new byte[4 * 4 * 4];

        ScreenCapture.Crop(source, 4, 4, 3, 3, 2, 2).ShouldBeNull();
        ScreenCapture.Crop(source, 4, 4, -1, 0, 1, 1).ShouldBeNull();
        ScreenCapture.Crop(source, 4, 4, 0, 0, 0, 1).ShouldBeNull();
    }
}
