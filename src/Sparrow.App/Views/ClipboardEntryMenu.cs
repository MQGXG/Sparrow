using Avalonia.Controls;
using Avalonia.Interactivity;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Views;

/// <summary>
/// 剪贴板条目的右键菜单。快速面板和工作区「剪贴板」页共用这一套结构与行为，
/// 差异只有两处：复制后是否收起窗口（面板收起、工作区不收起）、
/// 是否提供「粘贴到刚才的应用」（工作区窗口本身在前台，粘贴没有意义）。
///
/// 菜单项不写数据绑定：Avalonia 12 的 Flyout 在 Opened 阶段才有
/// PlacementTarget / DataContext，所以在打开后按条目刷新可见性与文案。
/// </summary>
public sealed class ClipboardEntryMenu
{
    private const string CopyPlainTag = "cmd:copy-plain";
    private const string TransformsTag = "cmd:transforms";
    private const string OpenTag = "cmd:open";
    private const string RevealTag = "cmd:reveal";
    private const string QueueTag = "cmd:queue";
    private const string FavoriteTag = "cmd:favorite";

    private readonly Func<ClipboardHistoryViewModel?> _history;
    private readonly Action<ClipboardEntryViewModel>? _showDetail;
    private readonly Action? _afterCopy;
    private readonly bool _includePaste;

    /// <summary>菜单打开时被右键的条目；菜单项 DataContext 取不到时用它兜底。</summary>
    private ClipboardEntryViewModel? _target;

    /// <param name="history">延迟取 ViewModel：窗口构造时 DataContext 往往还没设。</param>
    /// <param name="showDetail">打开页内详情。</param>
    /// <param name="afterCopy">复制类动作之后调用，面板用它收起窗口。</param>
    /// <param name="includePaste">是否提供「粘贴到刚才的应用」。</param>
    public ClipboardEntryMenu(
        Func<ClipboardHistoryViewModel?> history,
        Action<ClipboardEntryViewModel>? showDetail = null,
        Action? afterCopy = null,
        bool includePaste = true)
    {
        _history = history;
        _showDetail = showDetail;
        _afterCopy = afterCopy;
        _includePaste = includePaste;
    }

    /// <summary>给一行挂菜单；行上已有菜单时直接复用，避免模板重复实例化时叠加。</summary>
    public static void Attach(Control row, ClipboardEntryMenu menu)
    {
        row.ContextMenu ??= menu.Create();
    }

    public ContextMenu Create()
    {
        var menu = new ContextMenu();

        menu.Items.Add(Command("复制", (item, history) =>
        {
            history.SelectedItem = item;
            history.Copy(item);
            _afterCopy?.Invoke();
        }));

        if (_includePaste)
        {
            menu.Items.Add(Command("粘贴到刚才的应用", (item, history) =>
            {
                history.SelectedItem = item;
                PasteAfterFocusReturns(item, history, _afterCopy);
            }));
        }

        menu.Items.Add(Command("复制为纯文本", (item, history) =>
        {
            history.SelectedItem = item;
            history.CopyPlainText(item);
            _afterCopy?.Invoke();
        }, CopyPlainTag));

        menu.Items.Add(Transforms());

        menu.Items.Add(new Separator());
        menu.Items.Add(Command("打开", (item, _) => item.Open(), OpenTag));
        menu.Items.Add(Command("打开所在位置", (item, _) => item.RevealInExplorer(), RevealTag));

        menu.Items.Add(new Separator());
        menu.Items.Add(Command("加入顺序粘贴队列", (item, history) => history.ToggleQueue(item), QueueTag));
        menu.Items.Add(Command("收藏", (item, history) => history.ToggleFavorite(item), FavoriteTag));

        menu.Items.Add(new Separator());
        menu.Items.Add(Command("查看详情", (item, history) =>
        {
            history.SelectedItem = item;
            _showDetail?.Invoke(item);
        }));
        menu.Items.Add(Command("删除", (item, history) => history.Delete(item)));

        menu.Opened += (_, _) => Refresh(menu);
        return menu;
    }

    /// <summary>
    /// 粘贴：先收起窗口把焦点还给上一个应用，等一小段再注入 Ctrl+V。
    /// 不收起直接注入会把「粘贴」送给面板自己。
    /// </summary>
    public static async void PasteAfterFocusReturns(
        ClipboardEntryViewModel item,
        ClipboardHistoryViewModel history,
        Action? hideFirst = null)
    {
        hideFirst?.Invoke();

        // 窗口隐藏到系统把前台还给上一个窗口有一小段延迟，立刻发键会打到空处
        await Task.Delay(120);
        history.PasteItem(item);
    }

    private MenuItem Transforms()
    {
        var menu = new MenuItem { Header = "复制为", Tag = TransformsTag };

        AddTransform(menu, "全部大写", PasteTransform.Upper);
        AddTransform(menu, "全部小写", PasteTransform.Lower);
        AddTransform(menu, "每词首字母大写", PasteTransform.Capitalize);
        AddTransform(menu, "句首大写", PasteTransform.Sentence);
        AddTransform(menu, "驼峰命名", PasteTransform.Camel);
        AddTransform(menu, "去掉首尾空白", PasteTransform.Trim);

        return menu;
    }

    private void AddTransform(MenuItem parent, string header, PasteTransform transform)
    {
        var item = new MenuItem { Header = header, Tag = transform };

        item.Click += (_, e) =>
        {
            if (Target(item) is not { } entry || _history() is not { } history)
                return;

            history.SelectedItem = entry;
            history.CopyTransformed(entry, transform);
            _afterCopy?.Invoke();
            e.Handled = true;
        };

        parent.Items.Add(item);
    }

    private MenuItem Command(
        string header,
        Action<ClipboardEntryViewModel, ClipboardHistoryViewModel> action,
        string? tag = null)
    {
        var item = new MenuItem { Header = header };
        if (tag is not null)
            item.Tag = tag;

        item.Click += (_, e) =>
        {
            if (Target(item) is not { } entry || _history() is not { } history)
                return;

            action(entry, history);
            e.Handled = true;
        };

        return item;
    }

    /// <summary>打开后按条目刷新：文本专属项、有目标才显示的项、随状态变化的文案。</summary>
    private void Refresh(ContextMenu menu)
    {
        if ((menu.PlacementTarget?.DataContext ?? menu.DataContext) is not ClipboardEntryViewModel item)
        {
            _target = null;
            return;
        }

        _target = item;

        var isText = item.Entry.Kind == ClipboardEntryKind.Text
            && item.Entry.Text is { Length: > 0 };

        foreach (var entry in menu.Items)
        {
            if (entry is not MenuItem { Tag: string tag } menuItem)
                continue;

            switch (tag)
            {
                case CopyPlainTag:
                case TransformsTag:
                    menuItem.IsVisible = isText;
                    break;

                case OpenTag:
                    menuItem.IsVisible = item.CanOpen;
                    break;

                case RevealTag:
                    menuItem.IsVisible = item.CanReveal;
                    break;

                case QueueTag:
                    menuItem.Header = item.IsQueued ? "移出顺序粘贴队列" : "加入顺序粘贴队列";
                    break;

                case FavoriteTag:
                    menuItem.Header = item.IsFavorite ? "取消收藏" : "收藏";
                    break;
            }
        }
    }

    /// <summary>点击菜单项时取条目：优先从菜单项取，回退到打开时记住的那一个。</summary>
    private ClipboardEntryViewModel? Target(object? sender) => ItemFrom(sender) ?? _target;

    /// <summary>
    /// 右键菜单挂在弹出层里，DataContext 有时取不到条目；
    /// 先读菜单项自己，再沿父级向上找，最后回退到 ContextMenu 的 PlacementTarget。
    /// </summary>
    private static ClipboardEntryViewModel? ItemFrom(object? sender)
    {
        if (sender is not MenuItem menuItem)
            return null;

        if (menuItem.DataContext is ClipboardEntryViewModel direct)
            return direct;

        // 子菜单里的项，Parent 是父 MenuItem 而不是 ContextMenu
        for (var node = menuItem.Parent; node is not null; node = node.Parent)
        {
            if (node is Control { DataContext: ClipboardEntryViewModel fromParent })
                return fromParent;

            if (node is ContextMenu { PlacementTarget: Control target }
                && target.DataContext is ClipboardEntryViewModel fromTarget)
            {
                return fromTarget;
            }
        }

        return null;
    }
}
