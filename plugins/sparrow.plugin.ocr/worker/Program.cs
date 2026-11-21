using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;
using Sparrow.Ocr.Worker;
using Sparrow.Plugin.Ocr;

// OCR worker：PP-OCRv5 文字识别 + SLANet 表格结构。
//
// 协议（宿主与 worker 的唯一契约）：
//   args   = --language <code>（可选，默认 ch）
//            --mode text|table（可选，默认 text）
//   stdin  = 重复的 [4 字节小端长度][PNG/JPEG 图片字节]；关闭 stdin 表示结束
//   stdout = 每帧一行 JSON：
//            text 模式：{ text, regions[{ text, x, y, width, height, score }] }
//            table 模式：{ html: "<table>…</table>" }
//            单张识别失败时该行是 { "error": "原因" }
//   exit 0 = 正常结束；非 0 = 致命错误，stderr 带原因
//
// 为什么是多帧：批量识别要复用同一个进程，模型只在第一次识别时加载（1-3 秒）。
// 单张调用方只写一帧然后关闭 stdin，行为与旧协议一致。

var language = ParseLanguage(args);
var tableMode = ParseMode(args) == "table";

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

PaddleOcrAll? engine = null;
PaddleOcrTableRecognizer? tableRecognizer = null;

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
            using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (mat.Empty())
            {
                WriteLine(stdout, jsonOptions, new OcrWorkerErrorDto("无法解码图片"));
                continue;
            }

            if (tableMode)
            {
                // 表格结构（SLANet）：本地模型，不联网。
                // Sdcb 的后处理把归一化坐标直接乘原图宽高，只有正方形输入才成立，
                // 非正方形图会被压扁；这里统一补成正方形再识别，坐标仍可与原图对应。
                tableRecognizer ??= CreateTableRecognizer();
                using var square = PadToSquare(mat);
                var table = tableRecognizer.Run(square);

                if (table.HtmlTags.Count == 0)
                {
                    WriteLine(stdout, jsonOptions, new OcrWorkerErrorDto("没有识别出表格"));
                    continue;
                }

                // SLANet 只给格子结构，文字要用文字模型回填
                var textResult = EnsureTextEngine(language).Run(mat);
                var html = TableHtmlBuilder.Compose(
                    table.HtmlTags,
                    table.StructureBoxes.Select(box => new TableGridCell(
                        box.Rect.X, box.Rect.Y, box.Rect.Width, box.Rect.Height)).ToArray(),
                    textResult.Regions.Select(region =>
                    {
                        var rect = region.Rect.BoundingRect();
                        return new TableTextBlock(region.Text, rect.X, rect.Y, rect.Width, rect.Height);
                    }).ToArray());

                WriteLine(stdout, jsonOptions, new TableResultDto(html));
                continue;
            }

            var result = EnsureTextEngine(language).Run(mat);

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
    tableRecognizer?.Dispose();
}

static string ParseMode(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--mode")
            return args[i + 1];
    }

    return "text";
}

/// <summary>
/// 文字识别引擎：文字模式与表格模式（回填单元格）共用同一个实例。
/// 首次初始化约 1-3 秒，之后复用；本地模型，不联网。
/// </summary>
PaddleOcrAll EnsureTextEngine(string lang)
{
    engine ??= new PaddleOcrAll(SelectModel(lang), PaddleDevice.Mkldnn())
    {
        AllowRotateDetection = true,
        Enable180Classification = false
    };

    return engine;
}

/// <summary>表格模型随 worker 分发在 models/table 下（SLANet + 标签）。</summary>
static PaddleOcrTableRecognizer CreateTableRecognizer()
{
    var directory = Path.Combine(AppContext.BaseDirectory, "models", "table");
    var labels = Path.Combine(directory, "labels.txt");

    if (!File.Exists(labels))
        throw new FileNotFoundException("缺少表格模型标签文件", labels);

    return new PaddleOcrTableRecognizer(directory, labels, PaddleDevice.Mkldnn());
}

/// <summary>
/// 表格模型要求正方形输入：Sdcb 的坐标还原按「归一化值 × 原图宽高」算，
/// 非正方形图里的纵向坐标会被整体压扁，回填文字时就对不上格子。
/// 补黑边不改变原图坐标，文字框可以直接按原图坐标复用。
/// </summary>
static Mat PadToSquare(Mat src)
{
    if (src.Width == src.Height)
        return src.Clone();

    var side = Math.Max(src.Width, src.Height);
    var square = new Mat(new Size(side, side), src.Type(), Scalar.Black);
    using var roi = square.SubMat(0, src.Height, 0, src.Width);
    src.CopyTo(roi);
    return square;
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
