using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

// OCR worker：PP-OCRv5 文字识别。
//
// 协议（宿主与 worker 的唯一契约）：
//   stdin  = [4 字节小端长度][PNG/JPEG 图片字节]
//   stdout = UTF-8 识别文本
//   exit 0 = 成功；非 0 = 失败，stderr 带原因
//
// 之所以保持这个极简协议：换成 PaddleOCR-VL 或别的模型时，
// 宿主侧一行都不用改。

using var stdin = Console.OpenStandardInput();

var lengthBytes = new byte[4];
ReadExactly(stdin, lengthBytes);
var length = BitConverter.ToInt32(lengthBytes);

if (length <= 0)
{
    Console.Error.Write("图片长度为 0");
    return 2;
}

var imageBytes = new byte[length];
ReadExactly(stdin, imageBytes);

try
{
    // 本地模型，不联网；首次初始化约 1-3 秒
    using var engine = new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.Mkldnn())
    {
        AllowRotateDetection = true,
        Enable180Classification = false
    };

    using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
    if (mat.Empty())
    {
        Console.Error.Write("无法解码图片");
        return 3;
    }

    var result = engine.Run(mat);

    // 输出结构化结果：纯文本 + 每个文本块的坐标。
    // 只有坐标才能让插件还原空格/换行/阅读顺序；result.Text 是直接拼接的。
    var regions = result.Regions
        .Select(region =>
        {
            var rect = region.Rect.BoundingRect();
            return new OcrRegionDto(
                region.Text ?? string.Empty,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                region.Score);
        })
        .ToArray();

    var payload = new OcrResultDto(result.Text ?? string.Empty, regions);
    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var json = JsonSerializer.Serialize(payload, jsonOptions);

    var output = Encoding.UTF8.GetBytes(json);
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(output, 0, output.Length);
    return 0;
}
catch (Exception ex)
{
    // 模型/原生库出错时把原因带回宿主，而不是静默退出
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

internal sealed record OcrResultDto(string Text, OcrRegionDto[] Regions);

internal sealed record OcrRegionDto(
    string Text,
    int X,
    int Y,
    int Width,
    int Height,
    float Score);
