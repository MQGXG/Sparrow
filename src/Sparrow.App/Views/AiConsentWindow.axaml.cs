using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sparrow.App.Views;

/// <summary>
/// 发送前确认：展示服务、模型和即将发送的内容。
///
/// 只在需要时弹一次（勾了「本次运行不再提示」就不再弹），
/// 取消则本次请求直接作废，不会静默发出去。
/// </summary>
public partial class AiConsentWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();

    public AiConsentWindow()
    {
        InitializeComponent();
    }

    public AiConsentWindow(string endpoint, string model, string preview) : this()
    {
        EndpointText.Text = endpoint;
        ModelText.Text = model;
        PreviewText.Text = preview;
    }

    /// <summary>用户勾了「本次运行不再提示」。</summary>
    public bool Remember => RememberBox.IsChecked == true;

    /// <summary>true = 发送；false = 取消或直接关窗。</summary>
    public Task<bool> Result => _result.Task;

    protected override void OnClosed(EventArgs e)
    {
        // 右上角关窗等同于取消，避免调用方一直等
        _result.TrySetResult(false);
        base.OnClosed(e);
    }

    private void OnSendClick(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(false);
        Close();
    }
}
