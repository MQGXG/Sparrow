using Avalonia;
using Avalonia.Controls;

namespace Sparrow.Plugin.Ui;

/// <summary>
/// 宿主提供的一段带标题的内容区。插件往里放自己的控件组合，
/// 标题、间距与颜色由宿主样式统一，主题切换自动跟随。
/// </summary>
public class PluginSection : ContentControl
{
    /// <summary>段落标题。</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PluginSection, string?>(nameof(Title));

    /// <summary>段落标题。</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
}
