using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sparrow.App.Services;

namespace Sparrow.App.Views;

/// <summary>
/// 剪贴板详情的独立窗口容器。内容在 <see cref="ClipboardDetailView"/> 里，
/// 这里只负责窗口外观、Esc 关闭和「返回」后的关闭。
/// 数据仍由共享的 <c>ClipboardHistoryViewModel</c> 提供。
/// </summary>
public partial class ClipboardDetailWindow : Window
{
    public ClipboardDetailWindow()
    {
        InitializeComponent();

        // 隧道阶段拦 Esc：先用它退出大图，再关窗口，避免被输入框吃掉
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowChrome.TryApplyDarkTitleBar(this);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Close();
        e.Handled = true;
    }

    private void OnDetailCloseRequested(object? sender, EventArgs e) => Close();
}
