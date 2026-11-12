using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using Sparrow.Ocr.Worker;

// OCR worker：PP-OCRv5 文字识别。
//
// 协议（宿主与 worker 的唯一契约）：
//   args   = --language <code>（可选，默认 ch）
//   stdin  = [4 字节小端长度][PNG/JPEG 图片字节]
//   stdout = JSON：{ text, regions[{ text, x, y, width, height, score }] }
//   exit 0 = 成功；非 0 = 失败，stderr 带原因
//
// 之所以保持这个极简协议：换成 PaddleOCR-VL 或别的模型时，
// 宿主侧一行都不用改。

var language = ParseLanguage(args);

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
    using var engine = new PaddleOcrAll(SelectModel(language), PaddleDevice.Mkldnn())
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

            // Paddle 偶尔给出 NaN/Infinity 分数（空框、异常角度）。
            // System.Text.Json 无法写这种值，会让整个 worker 以 exit 4 失败；
            // 分数只用于置信度过滤，非法值按 0 处理。
            var score = float.IsFinite(region.Score) ? region.Score : 0f;

            return new OcrRegionDto(
                region.Text ?? string.Empty,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                score);
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

static string ParseLanguage(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--language")
            return args[i + 1];
    }

    return "ch";
}

static FullOcrModel SelectModel(string language) => language switch
{
    "en" => LocalFullModels.EnglishV5,
    "ko" => LocalFullModels.KoreanV5,
    "ru" or "uk" or "be" => LocalFullModels.EastSlavicV5,
    "bg" or "sr" or "mk" or "kk" or "ky" or "tg" or "mn" or "tt" or "ba" or "cv" or "udm" or "kv" or "os" or "bua" or "xal" or "tyv" or "sah" or "kaa"
        => LocalFullModels.CyrillicV5,
    "ar" or "fa" or "ur" or "ug" or "ps" or "sd" => LocalFullModels.ArabicV5,
    "th" => LocalFullModels.ThaiV5,
    "el" => LocalFullModels.GreekV5,
    "hi" or "mr" or "ne" => LocalFullModels.DevanagariV5,
    "te" => LocalFullModels.TeluguV5,
    "ta" => LocalFullModels.TamilV5,
    "fr" or "de" or "es" or "it" or "pt" or "nl" or "pl" or "tr" or "vi" => LocalFullModels.LatinV5,
    // ch（简体/繁体/拼音/英日）是默认，ja 也走这一套
    _ => LocalFullModels.ChineseV5
};
