namespace Sparrow.App.ViewModels;

/// <summary>表格结果的一列：显示文本 + 由宿主算好的像素宽度，保证各行对齐。</summary>
public sealed record ResultTableColumnViewModel(string Text, double Width);

/// <summary>表格结果的一个单元格。</summary>
public sealed record ResultTableCellViewModel(string Text, double Width);

/// <summary>表格结果的一行。</summary>
public sealed record ResultTableRowViewModel(IReadOnlyList<ResultTableCellViewModel> Cells);

/// <summary>文件集结果的一项：宿主只拿路径，名字 / 目录 / 大小由界面派生。</summary>
public sealed record ResultFileViewModel(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>文件在结果展示时是否已经不在了；界面用它显示「文件不存在」。</summary>
    public bool IsMissing => !System.IO.File.Exists(Path);

    /// <summary>可读的文件大小；文件不存在或读取失败时为空串。</summary>
    public string SizeLabel
    {
        get
        {
            try
            {
                var info = new System.IO.FileInfo(Path);
                return info.Exists ? FormatSize(info.Length) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };
}
