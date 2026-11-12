using System.Text;
using RapidOcrNet;
using SkiaSharp;

// 协议与 Sparrow.Ocr.Worker 保持一致：
//   stdin  = [4 字节小端长度][图片字节]
//   stdout = 纯文本
// 这样对比时可以直接复用同一个测试脚本。

using var stdin = Console.OpenStandardInput();

var lengthBytes = new byte[4];
ReadExactly(stdin, lengthBytes);
var length = BitConverter.ToInt32(lengthBytes);

if (length <= 0)
    return 2;

var imageBytes = new byte[length];
ReadExactly(stdin, imageBytes);

var baseDir = AppContext.BaseDirectory;
var det = Path.Combine(baseDir, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx");
var cls = Path.Combine(baseDir, "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
var rec = Path.Combine(baseDir, "models", "v5", "ch_PP-OCRv5_rec_mobile.onnx");
var dict = Path.Combine(baseDir, "models", "v5", "ppocrv5_dict.txt");

try
{
    using var ocr = new RapidOcr();
    ocr.InitModels(det, cls, rec, dict);

    using var bitmap = SKBitmap.Decode(imageBytes);
    if (bitmap is null)
        return 3;

    var result = ocr.Detect(bitmap, RapidOcrOptions.Default);
    var text = result.StrRes ?? string.Empty;

    var output = Encoding.UTF8.GetBytes(text);
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(output, 0, output.Length);
    return 0;
}
catch (Exception ex)
{
    Console.Error.Write(ex.ToString());
    return 4;
}

static void ReadExactly(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = stream.Read(buffer, offset, buffer.Length - offset);
        if (read == 0)
            throw new EndOfStreamException("stdin 提前结束");

        offset += read;
    }
}
