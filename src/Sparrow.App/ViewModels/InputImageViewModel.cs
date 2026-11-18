using Sparrow.Plugin.Sdk.Inputs;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 图片集里的一张图：插件要的输入 + 界面缩略图数据。
///
/// 宿主只读文件、不修改；<see cref="SourcePath"/> 仅用于去重与展示，
/// 不交给插件（插件拿到的是字节 + 名字）。
/// </summary>
public sealed class InputImageViewModel
{
    public InputImageViewModel(ImageInput input, string? sourcePath)
    {
        Input = input;
        SourcePath = sourcePath;
    }

    public ImageInput Input { get; }

    /// <summary>来自磁盘时是文件完整路径；剪贴板图片为 null。</summary>
    public string? SourcePath { get; }

    public string Name => Input.Name;

    /// <summary>缩略图字节：直接用原图编码，Avalonia 的位图解码器能处理 PNG / JPEG / WebP。</summary>
    public byte[] PreviewBytes => Input.Data.ToArray();
}
