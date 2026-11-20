using CommunityToolkit.Mvvm.ComponentModel;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App.ViewModels;

/// <summary>
/// 一个识别框：坐标 + 文字 + 多选状态。
///
/// 坐标原样保留（原图像素），显示时由 Viewbox 统一缩放。
/// 选中状态放在这里而不是 SDK 的 OcrTextLine 上——那是纯数据契约，不该带 UI 状态。
/// </summary>
public sealed partial class OcrLineViewModel : ObservableObject
{
    public OcrLineViewModel(OcrTextLine line)
    {
        Text = line.Text;
        X = line.X;
        Y = line.Y;
        Width = line.Width;
        Height = line.Height;
    }

    public string Text { get; }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>点框多选：选中后可以一起复制。</summary>
    [ObservableProperty]
    private bool _isSelected;
}
