using SkiaSharp;

namespace Sparrow.App.Services;

/// <summary>
/// 只读图片头拿宽高，不解码像素。
///
/// 用途：构造 <c>ImageInput</c> 时填 <c>Width</c>/<c>Height</c>。
/// 这两个值以前一直是 0，导致结果列表里的「展开看原图 + 识别框」
/// 永远被判定为「没有图」而不渲染。
/// </summary>
public static class ImageSizeReader
{
    public static (int Width, int Height) Read(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return (0, 0);

        try
        {
            // SKCodec 只解析文件头，不会把整张图解出来
            using var stream = new SKMemoryStream(data.ToArray());
            using var codec = SKCodec.Create(stream);

            if (codec is null)
                return (0, 0);

            var info = codec.Info;
            return (info.Width, info.Height);
        }
        catch
        {
            // 解不了尺寸不影响图片本身：只是叠不了识别框
            return (0, 0);
        }
    }
}
