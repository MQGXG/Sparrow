using System.Runtime.InteropServices;

namespace Sparrow.App.Services;

/// <summary>
/// 还原 Windows 放大镜的「全屏颜色效果」（反色 / 灰度等）。
///
/// 放大镜的效果由 DWM 在合成后应用，GDI 截到的是效果之后的画面；
/// 这里查 <c>MagGetFullscreenColorEffect</c> 拿到 5×5 颜色矩阵，
/// 求逆后逐像素还原。WGC 返回的本来就是原始像素，不需要这一步。
/// </summary>
internal sealed class MagnifierColorEffect
{
    private const int CacheMilliseconds = 2000;

    private static readonly object Gate = new();
    private static MagnifierColorEffect? _cached;
    private static bool _hasCache;
    private static long _cachedAt;

    private readonly float[] _rows;

    private MagnifierColorEffect(float[] rows) => _rows = rows;

    /// <summary>
    /// 当前放大镜颜色效果；没有效果 / 查询失败返回 null。
    /// 结果缓存 2 秒，避免每帧都初始化一遍放大镜 API。
    /// </summary>
    public static MagnifierColorEffect? Current
    {
        get
        {
            lock (Gate)
            {
                var now = Environment.TickCount64;

                if (_hasCache && now - _cachedAt < CacheMilliseconds)
                    return _cached;

                _cached = Query();
                _hasCache = true;
                _cachedAt = now;
                return _cached;
            }
        }
    }

    /// <summary>把逆变换应用到 BGRA 像素（alpha 不动）。</summary>
    public void Apply(byte[] bgra)
    {
        var r0 = _rows[0];
        var r1 = _rows[1];
        var r2 = _rows[2];
        var r3 = _rows[3];
        var g0 = _rows[4];
        var g1 = _rows[5];
        var g2 = _rows[6];
        var g3 = _rows[7];
        var b0 = _rows[8];
        var b1 = _rows[9];
        var b2 = _rows[10];
        var b3 = _rows[11];

        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            float blue = bgra[index];
            float green = bgra[index + 1];
            float red = bgra[index + 2];

            bgra[index] = Clamp(b0 * red + b1 * green + b2 * blue + b3);
            bgra[index + 1] = Clamp(g0 * red + g1 * green + g2 * blue + g3);
            bgra[index + 2] = Clamp(r0 * red + r1 * green + r2 * blue + r3);
        }
    }

    private static byte Clamp(float value) =>
        value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)value;

    private static MagnifierColorEffect? Query()
    {
        try
        {
            if (!MagInitialize())
                return null;

            try
            {
                var effect = new MAGCOLOREFFECT { transform = new float[25] };

                return MagGetFullscreenColorEffect(ref effect)
                    ? FromMatrix(effect.transform)
                    : null;
            }
            finally
            {
                MagUninitialize();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 MAGCOLOREFFECT 构造逆变换；单位矩阵 / 不支持的 alpha 效果返回 null。</summary>
    internal static MagnifierColorEffect? FromMatrix(float[] matrix)
    {
        if (matrix.Length != 25 || matrix.Any(value => !float.IsFinite(value)))
            return null;

        // 只支持 alpha 不变的效果：第 4 行和 4/5 列必须是单位矩阵
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 5; column++)
            {
                if (row != 3 && column < 3)
                    continue;

                var expected = row == column ? 1f : 0f;
                if (Math.Abs(matrix[row * 5 + column] - expected) > 1e-6)
                    return null;
            }
        }

        // 行向量约定 → 转成列向量矩阵
        var linear = new double[3, 3];

        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
                linear[row, column] = matrix[column * 5 + row];
        }

        var offset = new[]
        {
            (double)matrix[20],
            (double)matrix[21],
            (double)matrix[22]
        };

        if (IsIdentity(linear) && offset.All(value => Math.Abs(value) < 1e-9))
            return null;

        if (!TryInvert(linear, out var inverse))
            return null;

        // 数值稳定性：条件数太差的矩阵不猜
        if (Norm(linear) * Norm(inverse) > 10_000)
            return null;

        var translation = new[]
        {
            -(inverse[0, 0] * offset[0] + inverse[0, 1] * offset[1] + inverse[0, 2] * offset[2]),
            -(inverse[1, 0] * offset[0] + inverse[1, 1] * offset[1] + inverse[1, 2] * offset[2]),
            -(inverse[2, 0] * offset[0] + inverse[2, 1] * offset[1] + inverse[2, 2] * offset[2])
        };

        var rows = new float[12];

        for (var row = 0; row < 3; row++)
        {
            rows[row * 4] = (float)inverse[row, 0];
            rows[row * 4 + 1] = (float)inverse[row, 1];
            rows[row * 4 + 2] = (float)inverse[row, 2];
            rows[row * 4 + 3] = (float)(translation[row] * 255.0);
        }

        if (rows.Any(value => !float.IsFinite(value)))
            return null;

        return new MagnifierColorEffect(rows);
    }

    private static bool IsIdentity(double[,] matrix)
    {
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                if (Math.Abs(matrix[row, column] - (row == column ? 1 : 0)) > 1e-9)
                    return false;
            }
        }

        return true;
    }

    private static bool TryInvert(double[,] matrix, out double[,] inverse)
    {
        inverse = new double[3, 3];

        var determinant =
            matrix[0, 0] * (matrix[1, 1] * matrix[2, 2] - matrix[1, 2] * matrix[2, 1])
            - matrix[0, 1] * (matrix[1, 0] * matrix[2, 2] - matrix[1, 2] * matrix[2, 0])
            + matrix[0, 2] * (matrix[1, 0] * matrix[2, 1] - matrix[1, 1] * matrix[2, 0]);

        if (Math.Abs(determinant) < 1e-9)
            return false;

        var scale = 1.0 / determinant;

        inverse[0, 0] = (matrix[1, 1] * matrix[2, 2] - matrix[1, 2] * matrix[2, 1]) * scale;
        inverse[0, 1] = (matrix[0, 2] * matrix[2, 1] - matrix[0, 1] * matrix[2, 2]) * scale;
        inverse[0, 2] = (matrix[0, 1] * matrix[1, 2] - matrix[0, 2] * matrix[1, 1]) * scale;
        inverse[1, 0] = (matrix[1, 2] * matrix[2, 0] - matrix[1, 0] * matrix[2, 2]) * scale;
        inverse[1, 1] = (matrix[0, 0] * matrix[2, 2] - matrix[0, 2] * matrix[2, 0]) * scale;
        inverse[1, 2] = (matrix[0, 2] * matrix[1, 0] - matrix[0, 0] * matrix[1, 2]) * scale;
        inverse[2, 0] = (matrix[1, 0] * matrix[2, 1] - matrix[1, 1] * matrix[2, 0]) * scale;
        inverse[2, 1] = (matrix[0, 1] * matrix[2, 0] - matrix[0, 0] * matrix[2, 1]) * scale;
        inverse[2, 2] = (matrix[0, 0] * matrix[1, 1] - matrix[0, 1] * matrix[1, 0]) * scale;

        return true;
    }

    private static double Norm(double[,] matrix)
    {
        var max = 0.0;

        for (var row = 0; row < 3; row++)
        {
            var sum = Math.Abs(matrix[row, 0]) + Math.Abs(matrix[row, 1]) + Math.Abs(matrix[row, 2]);
            max = Math.Max(max, sum);
        }

        return max;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGCOLOREFFECT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] transform;
    }

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagGetFullscreenColorEffect(ref MAGCOLOREFFECT effect);
}
