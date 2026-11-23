using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>放大镜全屏颜色滤镜的逆变换。</summary>
public sealed class MagnifierColorEffectTests
{
    private static float[] Identity() =>
    [
        1, 0, 0, 0, 0,
        0, 1, 0, 0, 0,
        0, 0, 1, 0, 0,
        0, 0, 0, 1, 0,
        0, 0, 0, 0, 1
    ];

    [Fact]
    public void 单位矩阵_不还原()
    {
        MagnifierColorEffect.FromMatrix(Identity()).ShouldBeNull();
    }

    [Fact]
    public void 反色矩阵_把像素还原回来()
    {
        float[] invert =
        [
            -1, 0, 0, 0, 0,
            0, -1, 0, 0, 0,
            0, 0, -1, 0, 0,
            0, 0, 0, 1, 0,
            1, 1, 1, 0, 1
        ];

        var effect = MagnifierColorEffect.FromMatrix(invert).ShouldNotBeNull();

        // BGRA
        var pixels = new byte[] { 10, 20, 30, 255 };
        effect.Apply(pixels);

        pixels[0].ShouldBe((byte)245);
        pixels[1].ShouldBe((byte)235);
        pixels[2].ShouldBe((byte)225);
        pixels[3].ShouldBe((byte)255);
    }

    [Fact]
    public void alpha行不是单位矩阵_不处理()
    {
        var matrix = Identity();
        matrix[15] = 0.5f;   // 第 4 行

        MagnifierColorEffect.FromMatrix(matrix).ShouldBeNull();
    }

    [Fact]
    public void 奇异矩阵_不处理()
    {
        float[] singular =
        [
            1, 2, 0, 0, 0,
            2, 4, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 1, 0,
            0, 0, 0, 0, 1
        ];

        MagnifierColorEffect.FromMatrix(singular).ShouldBeNull();
    }
}
