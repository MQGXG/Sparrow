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
//   stdin  = 重复的 [4 字节小端长度][PNG/JPEG 图片字节]；关闭 stdin 表示结束
//   stdout = 每帧一行 JSON：{ text, regions[{ text, x, y, width, height, score }] }
//            单张识别失败时该行是 { "error": "原因" }
//   exit 0 = 正常结束；非 0 = 致命错误，stderr 带原因
//
// 为什么是多帧：批量识别要复用同一个进程，模型只在第一次识别时加载（1-3 秒）。
// 单张调用方只写一帧然后关闭 stdin，行为与旧协议一致。

var language = ParseLanguage(args);

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

PaddleOcrAll? engine = null;

try
{
    var lengthBytes = new byte[4];

    while (TryReadExactly(stdin, lengthBytes))
    {
        var length = BitConverter.ToInt32(lengthBytes);

        if (length <= 0)
        {
            WriteLine(stdout, jsonOptions, new OcrWorkerErrorDto("图片长度为 0"));
            continue;
        }

        var imageBytes = new byte[length];
        if (!TryReadExactly(stdin, imageBytes))
        {
            WriteLine(stdout, jsonOptions, new OcrWorkerErrorDto("stdin 提前结束"));
            break;
        }

        try
        {
            // 本地模型，不联网；首次初始化约 1-3 秒，之后复用
            engine ??= new PaddleOcrAll(SelectModel(language), PaddleDevice.Mkldnn())
            {
                AllowRotateDetection = true,
                Enable180Classification = false
            };

            using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (mat.Empty())
            {
                WriteLine(stdout, jsonOptions, new OcrWorkerErrorDto("无法解码图片"));
                continue;
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

            WriteLine(stdout, jsonOptions, new OcrResultDto(result.Text ?? string.Empty, regions));
        }
        catch (Exception ex)
        {
            // 单张失败不终止进程：批量还要继续识别后面的图
            WriteLine(stdout, jsonOptions, new OcrWorkerErrorDto(ex.GetBaseException().Message));
        }
    }

    return 0;
}
catch (Exception ex)
{
    // 模型/原生库出错时把原因带回宿主，而不是静默退出
    Console.Error.Write(ex.ToString());
    return 4;
}
finally
{
    engine?.Dispose();
}

static bool TryReadExactly(Stream stream, byte[] buffer)
{
    var offset = 0;

    while (offset < buffer.Length)
    {
        var read = stream.Read(buffer, offset, buffer.Length - offset);

        // 干净 EOF：一帧都没开始，正常结束
        if (read == 0)
        {
            if (offset == 0)
                return false;

            throw new EndOfStreamException("stdin 提前结束");
        }

        offset += read;
    }

    return true;
}

static void WriteLine<T>(Stream stdout, JsonSerializerOptions options, T payload)
{
    var json = JsonSerializer.Serialize(payload, options);
    var bytes = Encoding.UTF8.GetBytes(json + "\n");
    stdout.Write(bytes, 0, bytes.Length);
    stdout.Flush();
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
