using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;

namespace Sparrow.App.Views;

/// <summary>
/// 双击 Ctrl 唤出的文件搜索框。
///
/// code-behind 只处理窗口行为：显示、隐藏、焦点、键盘、打开文件。
/// 查询和结果集合在 <see cref="QuickSearchViewModel"/>。
/// </summary>
public partial class QuickSearchWindow : Window
{
    // 刚 Show 时系统可能立刻触发一次 Deactivated（焦点竞争），这段窗口内的失焦不算
    private const int DeactivateGraceMilliseconds = 250;

    private DateTime _shownAt;

    /// <summary>失焦时是否自动隐藏；--quick-search 调试/快捷方式入口会关掉。</summary>
    public bool AutoHide { get; set; } = true;

    /// <summary>设计器需要的无参构造。</summary>
    public QuickSearchWindow()
    {
        InitializeComponent();
        Deactivated += OnWindowDeactivated;

        // 隧道阶段拦：TextBox 会吃掉上下键（移动光标）和 Enter
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    public QuickSearchWindow(IFileIndex index) : this()
    {
        DataContext = new QuickSearchViewModel(index);
    }

    /// <summary>双击 Ctrl 入口：已显示时再按一次等于关闭。</summary>
    public void Toggle()
    {
        App.Log.Info($"文件搜索窗口：{(IsVisible ? "关闭" : "打开")}");

        if (IsVisible)
        {
            Hide();
            return;
        }

        if (DataContext is QuickSearchViewModel viewModel)
            viewModel.Reset();

        ShowShell();

        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ShowShell()
    {
        _shownAt = DateTime.UtcNow;

        // 先摆到「低 8px + 透明」，Show 后再过渡到正常位置
        Shell.Opacity = 0;
        if (Shell.RenderTransform is TranslateTransform transform)
            transform.Y = 8;

        Show();
        Activate();

        Dispatcher.UIThread.Post(() =>
        {
            Shell.Opacity = 1;
            if (Shell.RenderTransform is TranslateTransform shown)
                shown.Y = 0;
        }, DispatcherPriority.Render);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!AutoHide)
            return;

        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < DeactivateGraceMilliseconds)
            return;

        Hide();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // 输入法正在处理这个键：绝不抢
        if (e.Key == Key.ImeProcessed)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                return;

            case Key.Enter:
                OpenSelected(e.KeyModifiers.HasFlag(KeyModifiers.Control));
                e.Handled = true;
                return;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                return;

            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                return;

            case Key.PageUp:
                MoveSelection(-10);
                e.Handled = true;
                return;

            case Key.PageDown:
                MoveSelection(10);
                e.Handled = true;
                return;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = ResultList.ItemCount;

        if (count == 0)
            return;

        var index = Math.Clamp(ResultList.SelectedIndex + delta, 0, count - 1);
        ResultList.SelectedIndex = index;
        ResultList.ScrollIntoView(index);
    }

    /// <summary>回车打开文件/文件夹；Ctrl+Enter 在资源管理器中定位。</summary>
    private void OpenSelected(bool reveal)
    {
        if (DataContext is not QuickSearchViewModel { Selected: { } selected })
            return;

        try
        {
            if (reveal)
            {
                Process.Start(new ProcessStartInfo(
                    "explorer.exe",
                    $"/select,\"{selected.FullPath}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(selected.FullPath)
                {
                    UseShellExecute = true
                });
            }

            Hide();
        }
        catch (Exception ex)
        {
            App.Log.Error($"打开文件失败：{selected.FullPath}", ex);
        }
    }
}
