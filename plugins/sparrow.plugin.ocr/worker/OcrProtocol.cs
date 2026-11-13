namespace Sparrow.Ocr.Worker;

/// <summary>
/// worker 的 stdout JSON 协议：纯文本 + 每个文本块的结构化信息。
///
/// 与 plugins/sparrow.plugin.ocr/OcrWorkerResult.cs 一一对应。
/// 两边必须同步修改——属性名由 CamelCase 策略生成，
/// 改了顺序或名字，插件侧就会解析失败。
/// </summary>
internal sealed record OcrResultDto(string Text, OcrRegionDto[] Regions);

/// <summary>一个文本块及其包围盒（图片物理像素坐标）。</summary>
internal sealed record OcrRegionDto(
    string Text,
    int X,
    int Y,
    int Width,
    int Height,
    float Score);
