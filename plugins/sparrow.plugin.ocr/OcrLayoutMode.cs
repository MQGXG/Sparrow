namespace Sparrow.Plugin.Ocr;

/// <summary>
/// OCR 结果的整理模式。
/// 算法移植自 OmniSeeker 的 <c>OcrTextSorter</c>：同一套行聚类，
/// 三种不同的输出方式；表格模式用更严格的行阈值防止两行被并成一行。
/// </summary>
public enum OcrLayoutMode
{
    /// <summary>按行：从上到下聚行，行内从左到右，块间用空格（默认，最通用）。</summary>
    Line,

    /// <summary>原文：worker 直接给出的全文，不做任何重排。</summary>
    Raw,

    /// <summary>按位置：用空格近似还原每个块的水平位置，保留原始排版观感。</summary>
    Position,

    /// <summary>表格：更严格的行判定，列间用 Tab 分隔，可直接粘贴到 Excel。</summary>
    Table
}
