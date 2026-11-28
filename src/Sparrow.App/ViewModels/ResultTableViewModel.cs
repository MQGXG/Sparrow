namespace Sparrow.App.ViewModels;

/// <summary>表格结果的一列：显示文本 + 由宿主算好的像素宽度，保证各行对齐。</summary>
public sealed record ResultTableColumnViewModel(string Text, double Width);

/// <summary>表格结果的一个单元格。</summary>
public sealed record ResultTableCellViewModel(string Text, double Width);

/// <summary>表格结果的一行。</summary>
public sealed record ResultTableRowViewModel(IReadOnlyList<ResultTableCellViewModel> Cells);

/// <summary>文件集结果的一项：宿主只拿路径，名字 / 目录由界面派生。</summary>
public sealed record ResultFileViewModel(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}
