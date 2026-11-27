using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sparrow.App.Views;

/// <summary>
/// 局域网互传的接收确认框。
/// 只负责展示发送方和文件名，结果由调用方读取 <see cref="Accepted"/>。
/// </summary>
public partial class LanShareConfirmWindow : Window
{
    public bool Accepted { get; private set; }

    public LanShareConfirmWindow()
        : this("未知设备", [])
    {
    }

    public LanShareConfirmWindow(string sender, IReadOnlyList<string> files)
    {
        InitializeComponent();
        SenderText.Text = $"来自：{sender}";
        FileList.ItemsSource = files;

        // 标题栏跟随应用主题
        Opened += (_, _) => Services.WindowChrome.TryApplyTitleBarTheme(
            this,
            !Equals(ActualThemeVariant, Avalonia.Styling.ThemeVariant.Light));
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnDeclineClick(object? sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
