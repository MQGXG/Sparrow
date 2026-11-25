using Avalonia.Controls;
using Sparrow.App.Views;

namespace Sparrow.App.Shell;

/// <summary>
/// 托盘图标与右键菜单。
/// 托盘是本应用的「常驻入口」：主窗口从不自动显示，一切从托盘或热键开始。
///
/// 菜单内容来自 <c>settings.json</c> 的 <c>trayMenu</c>（见 <see cref="TrayMenuPlanner"/>）；
/// 没配置时与内置默认一致。
/// </summary>
public sealed class TrayService
{
    private readonly PaletteWindow _palette;
    private readonly Action _showWorkspace;
    private readonly Action _capture;
    private readonly Action _record;
    private readonly IReadOnlyList<TrayMenuEntry> _menuEntries;
    private readonly Action<string> _executeToolHotkey;
    private TrayIcon? _icon;

    public TrayService(
        PaletteWindow palette,
        Action showWorkspace,
        Action capture,
        Action record,
        IReadOnlyList<TrayMenuEntry> menuEntries,
        Action<string> executeToolHotkey)
    {
        _palette = palette;
        _showWorkspace = showWorkspace;
        _capture = capture;
        _record = record;
        _menuEntries = menuEntries;
        _executeToolHotkey = executeToolHotkey;
    }

    public void Initialize()
    {
        var menu = new NativeMenu();

        foreach (var entry in _menuEntries)
        {
            switch (entry.Kind)
            {
                case TrayMenuActionKind.Separator:
                    menu.Add(new NativeMenuItemSeparator());
                    break;

                case TrayMenuActionKind.Palette:
                    menu.Add(CreateItem(entry.Label, () => _palette.Toggle()));
                    break;

                case TrayMenuActionKind.Capture:
                    menu.Add(CreateItem(entry.Label, _capture));
                    break;

                case TrayMenuActionKind.Record:
                    menu.Add(CreateItem(entry.Label, _record));
                    break;

                case TrayMenuActionKind.Workspace:
                    menu.Add(CreateItem(entry.Label, _showWorkspace));
                    break;

                case TrayMenuActionKind.Exit:
                    menu.Add(CreateItem(entry.Label, ExitApplication));
                    break;

                case TrayMenuActionKind.Tool when !string.IsNullOrEmpty(entry.ToolId):
                    var toolId = entry.ToolId;
                    menu.Add(CreateItem(entry.Label, () => _executeToolHotkey(toolId)));
                    break;
            }
        }

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

    private static NativeMenuItem CreateItem(string label, Action onClick)
    {
        var item = new NativeMenuItem(label);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void ExitApplication() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.Shutdown();

    /// <summary>热键注册失败等异常状态，直接反映到托盘提示上。</summary>
    public void SetStatus(string text)
    {
        if (_icon is not null)
            _icon.ToolTipText = text;
    }
}
