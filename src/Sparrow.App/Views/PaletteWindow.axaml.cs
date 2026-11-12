using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

/// <summary>
/// 命令面板窗口。
///
/// code-behind 只处理「窗口行为」：显示、隐藏、焦点、键盘快捷键。
/// 搜索和执行逻辑全部在 <see cref="PaletteViewModel"/>，避免业务渗透到 View。
/// </summary>
public partial class PaletteWindow : Window
{
    // 刚 Show 时系统可能立刻触发一次 Deactivated（焦点竞争），
    // 这个时间窗内的失焦不算「用户点到别处」
    private const int DeactivateGraceMilliseconds = 250;

    // 点托盘时窗口会先失焦隐藏，紧接着托盘 Clicked 又调用一次 Toggle；
    // 没有这个时间窗，用户想「关掉」却会看到面板重新弹出来
    private const int ReopenGuardMilliseconds = 200;

    private DateTime _shownAt;
    private DateTime _lastDeactivatedAt = DateTime.MinValue;

    public PaletteWindow()
    {
        InitializeComponent();
        Deactivated += OnWindowDeactivated;

        // 快捷键全部走隧道阶段：
        // Ctrl+C 会被 TextBox 的复制命令吃掉，Enter 会被 TextBox 标成已处理，
        // 冒泡阶段（哪怕 handledEventsToo）都不一定拿得到。
        // 面板只有一个搜索框，在窗口层先拦不会误伤别的控件。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>热键 / 托盘入口。已显示时再按一次等于关闭。</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        // 刚刚因失焦隐藏（典型场景是点击托盘图标）：这次点击的意图是关掉，不再重开
        if ((DateTime.UtcNow - _lastDeactivatedAt).TotalMilliseconds < ReopenGuardMilliseconds)
            return;

        if (DataContext is PaletteViewModel viewModel)
            viewModel.Reset();

        _shownAt = DateTime.UtcNow;
        Show();
        Activate();

        // 焦点必须落在搜索框上，否则用户要先点一下才能打字
        SearchBox.Focus();
        SearchBox.SelectAll();
        App.Log.Info($"面板已显示，搜索框焦点={SearchBox.IsFocused}");
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < DeactivateGraceMilliseconds)
            return;

        _lastDeactivatedAt = DateTime.UtcNow;
        Hide();
    }

    /// <summary>
    /// 隧道阶段拦截快捷键。
    /// 注意：Enter 在这里会先于输入法拿到——中文输入法用 Enter 选词时可能受影响。
    /// M1 先保证「英文搜工具 + 回车执行」可用，IME 细调放到 M1 后半段。
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // IME 正在处理这个键：绝不抢。
        // 中文输入法会把字母和第一次 Enter 都标记成 ImeProcessed——
        // 那次 Enter 的职责是「把组合串上屏」，不是「执行工具」。
        // 抢了它就会先执行、再被上屏触发的搜索刷新清空结果。
        if (e.Key == Key.ImeProcessed)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                return;

            case Key.Enter when DataContext is PaletteViewModel executeTarget:
                App.Log.Info("快捷键 Enter：执行当前工具");
                _ = executeTarget.ExecuteAsync();
                e.Handled = true;
                return;

            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control)
                             && DataContext is PaletteViewModel copyTarget:
                copyTarget.CopyResult();
                App.Log.Info("快捷键 Ctrl+C：复制结果");
                e.Handled = true;
                return;
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PaletteViewModel viewModel)
            viewModel.CopyResult();
    }
}
