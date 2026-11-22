using System.Runtime.InteropServices;
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
    public static (int Width, int Height) Read(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            return (0, 0);

        // 常见调用来自完整 byte[]：交给 SKMemoryStream 只读，不再复制整张图。
        // 非零偏移的切片（少见）退回复制，避免为了头信息引入 unsafe。
        if (MemoryMarshal.TryGetArray(data, out var segment)
            && segment.Array is { } array
            && segment.Offset == 0
            && segment.Count == array.Length)
        {
            using var stream = new SKMemoryStream(array);
            return Read(stream);
        }

        return Read(new SKMemoryStream(data.ToArray()));
    }

    private static (int Width, int Height) Read(SKMemoryStream stream)
    {
        try
        {
            // SKCodec 只解析文件头，不会把整张图解出来
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
