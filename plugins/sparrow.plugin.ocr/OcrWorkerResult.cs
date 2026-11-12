namespace Sparrow.Plugin.Ocr;

/// <summary>
/// OCR worker 的结构化输出。
/// 纯文本只能「大概对」，坐标才能还原空格、换行和阅读顺序。
/// </summary>
public sealed record OcrWorkerResult(string Text, IReadOnlyList<OcrRegion> Regions);

/// <summary>一个文本块及其在图片中的包围盒。</summary>
public sealed record OcrRegion(
    string Text,
    int X,
    int Y,
    int Width,
    int Height,
    float Score);
