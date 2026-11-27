using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sparrow.App.Services;
using Sparrow.App.Services.Recording;
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

    // 结果项拖拽：按下记录起点，移动超过阈值才启动，避免普通点击被当成拖拽
    private PointerPressedEventArgs? _dragPress;
    private FileSearchItemViewModel? _dragItem;
    private Point _dragOrigin;
    private bool _dragStarted;

    // 原生 Shell 菜单打开期间不要因失焦隐藏窗口
    private bool _menuOpen;

    // 复制完整路径用宿主现有的 Win32 剪贴板实现（Avalonia 12 的 IClipboard 没有 SetTextAsync）
    private readonly Win32Clipboard _clipboard = new();

    /// <summary>设计器需要的无参构造。</summary>
    public QuickSearchWindow()
    {
        InitializeComponent();
        Deactivated += OnWindowDeactivated;

        // 隧道阶段拦：TextBox 会吃掉上下键（移动光标）和 Enter
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // ListBoxItem 会处理掉右键的 PointerPressed，用 ContextRequested 才能在隧道阶段拿到
        AddHandler(
            ContextRequestedEvent,
            OnResultContextRequested,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // ListBoxItem 也会把左键的 PointerPressed 标记为已处理，
        // 拖拽起点和单击打开必须挂 handledEventsToo 才收得到
        AddHandler(PointerPressedEvent, OnResultPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnResultPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnResultPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

        // 无边框窗口：按在输入框、列表、按钮以外的空白处可以拖动窗口
        AddHandler(PointerPressedEvent, OnShellPointerPressed, RoutingStrategies.Tunnel);
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
        if (!AutoHide || _menuOpen)
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

            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                // 搜索框聚焦时，Ctrl+C 保留给输入框复制文本
                if (SearchBox.IsFocused && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    return;

                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    CopySelectedPath();
                else
                    CopySelectedFile();

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

    /// <summary>Ctrl+C：把选中的文件/文件夹放进剪贴板，资源管理器可直接粘贴。</summary>
    private void CopySelectedFile()
    {
        if (DataContext is not QuickSearchViewModel { Selected: { } selected } viewModel)
            return;

        var copied = RecordingClipboard.CopyPaths([selected.FullPath]);

        viewModel.StatusText = copied
            ? $"已复制：{selected.Name}"
            : "复制失败（文件可能已不存在）";
    }

    /// <summary>Ctrl+Shift+C：复制完整路径文本。</summary>
    private void CopySelectedPath()
    {
        if (DataContext is not QuickSearchViewModel { Selected: { } selected } viewModel)
            return;

        var copied = _clipboard.SetText(selected.FullPath);
        viewModel.StatusText = copied ? "已复制完整路径" : "复制路径失败";
    }

    private void OnResultPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ResultList).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is not Control { DataContext: FileSearchItemViewModel item })
            return;

        _dragPress = e;
        _dragItem = item;
        _dragOrigin = e.GetPosition(this);
        _dragStarted = false;
    }

    /// <summary>
    /// 无边框窗口的拖动：按在输入框、结果列表、按钮以外的空白处时移动窗口。
    /// 顶部留白、搜索框内边距、底部状态栏都可以当拖动条。
    /// </summary>
    private void OnShellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (IsInteractiveSource(e.Source))
            return;

        BeginMoveDrag(e);
    }

    /// <summary>点击是否落在自己处理鼠标的控件里（输入框、列表、按钮、滚动条）。</summary>
    private static bool IsInteractiveSource(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TextBox or Button or ListBox or ListBoxItem or ScrollBar or Thumb)
                return true;
        }

        return false;
    }

    /// <summary>右键结果项：弹原生 Shell 上下文菜单。</summary>
    private void OnResultContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control { DataContext: FileSearchItemViewModel item })
            return;

        App.Log.Info($"右键菜单：{item.FullPath}");

        if (DataContext is QuickSearchViewModel viewModel)
            viewModel.Selected = item;

        ShowShellMenu(item);
        e.Handled = true;
    }

    /// <summary>在鼠标位置弹出原生 Shell 上下文菜单；菜单关闭后刷新结果。</summary>
    private void ShowShellMenu(FileSearchItemViewModel item)
    {
        var handle = TryGetPlatformHandle();

        if (handle is null || handle.Handle == IntPtr.Zero)
            return;

        var cursor = WheelScroller.GetCursorPosition();

        if (cursor is null)
            return;

        App.Log.Info($"原生菜单：hwnd={handle.Handle} cursor={cursor.Value.X},{cursor.Value.Y} path={item.FullPath}");

        _menuOpen = true;

        try
        {
            var shown = ShellContextMenu.Show(item.FullPath, cursor.Value.X, cursor.Value.Y);
            App.Log.Info($"原生菜单返回：{shown}");
        }
        finally
        {
            _menuOpen = false;

            Dispatcher.UIThread.Post(() =>
            {
                Activate();
                SearchBox.Focus();

                if (DataContext is QuickSearchViewModel viewModel)
                    viewModel.Refresh();
            });
        }
    }

    private async void OnResultPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPress is null || _dragItem is null || _dragStarted)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetDrag();
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragOrigin.X) < 8
            && Math.Abs(position.Y - _dragOrigin.Y) < 8)
        {
            return;
        }

        _dragStarted = true;

        var press = _dragPress;
        var item = _dragItem;

        try
        {
            var drag = await ClipboardDragService.CreateFilesAsync(
                [item.FullPath],
                StorageProvider,
                ClipboardDragMode.FileOnly);

            if (drag is null)
                return;

            try
            {
                await DragDrop.DoDragDropAsync(
                    press, drag.Transfer, DragDropEffects.Copy);
            }
            finally
            {
                drag.Lifetime?.Dispose();
            }
        }
        catch
        {
            // 目标应用拒绝拖放，不影响搜索
        }
        finally
        {
            ResetDrag();
        }
    }

    private void OnResultPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var item = _dragItem;
        var wasDragged = _dragStarted;

        ResetDrag();

        // 没有发生拖拽的左键释放 = 单击打开（拖拽时保持拖放，不打开）
        if (wasDragged
            || item is null
            || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (DataContext is QuickSearchViewModel viewModel)
        {
            viewModel.Selected = item;
            OpenSelected(false);
        }
    }

    private void ResetDrag()
    {
        _dragPress = null;
        _dragItem = null;
        _dragStarted = false;
    }

    /// <summary>回车打开文件/文件夹；Ctrl+Enter 在资源管理器中定位。</summary>
    private void OpenSelected(bool reveal)
    {
        if (DataContext is not QuickSearchViewModel { Selected: { } selected })
            return;

        try
        {
            App.Log.Info($"文件搜索打开{(reveal ? "并定位" : string.Empty)}：{selected.FullPath}");

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
