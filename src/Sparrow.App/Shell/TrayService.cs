using Avalonia.Controls;
using Sparrow.App.Views;

namespace Sparrow.App.Shell;

/// <summary>
/// 托盘图标与右键菜单。
/// 托盘是本应用的「常驻入口」：主窗口从不自动显示，一切从托盘或热键开始。
/// </summary>
public sealed class TrayService
{
    private readonly PaletteWindow _palette;
    private readonly Action _showWorkspace;
    private readonly Action _capture;
    private TrayIcon? _icon;

    public TrayService(PaletteWindow palette, Action showWorkspace, Action capture)
    {
        _palette = palette;
        _showWorkspace = showWorkspace;
        _capture = capture;
    }

    public void Initialize()
    {
        var menu = new NativeMenu();

        var search = new NativeMenuItem("搜索工具…    Ctrl+Alt+S");
        search.Click += (_, _) => _palette.Toggle();
        menu.Add(search);

        var capture = new NativeMenuItem("截图识别…    Ctrl+Alt+C");
        capture.Click += (_, _) => _capture();
        menu.Add(capture);

        var workspace = new NativeMenuItem("工作区…");
        workspace.Click += (_, _) => _showWorkspace();
        menu.Add(workspace);

        menu.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) =>
            (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.Shutdown();
        menu.Add(exit);

        _icon = new TrayIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "Sparrow · 麻雀",
            Menu = menu,
            IsVisible = true
        };

        // 左键单击唤出面板（Windows 上 Clicked 即左键）
        _icon.Clicked += (_, _) => _palette.Toggle();

        // Avalonia 12：new TrayIcon 不会自动挂到应用上。
        // 必须放进 Application 的 TrayIcons 集合，Attach() 才会创建原生图标。
        // 少了这一步：进程正常、日志正常，但托盘里什么都没有。
        if (Avalonia.Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
            icons.Add(_icon);
            TrayIcon.SetIcons(app, icons);
        }
    }

    /// <summary>热键注册失败等异常状态，直接反映到托盘提示上。</summary>
    public void SetStatus(string text)
    {
        if (_icon is not null)
            _icon.ToolTipText = text;
    }
}
