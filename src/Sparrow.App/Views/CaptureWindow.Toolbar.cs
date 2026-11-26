// CaptureWindow 分部类：工具注册表、分组折叠、选项条弹层与按钮状态。
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Payloads;
using SkiaSharp;

namespace Sparrow.App.Views;

public partial class CaptureWindow
{
    private void SetTool(AnnotationTool tool)
    {
        var next = _tool == tool ? AnnotationTool.None : tool;

        if (next != AnnotationTool.Angle)
            CancelAngleDraft();

        _tool = next;

        // 切到画图工具时取消选中，避免虚线框挡着下一笔
        if (_tool != AnnotationTool.None)
            SelectNote(-1);

        UpdateToolButtons();
    }

    /// <summary>直接设成某个工具（不做「再点一次取消」的切换语义），用于临时让位。</summary>
    private void ForceTool(AnnotationTool tool)
    {
        if (tool != AnnotationTool.Angle)
            CancelAngleDraft();

        _tool = tool;

        if (tool != AnnotationTool.None)
            SelectNote(-1);

        UpdateToolButtons();
    }

    // ---------- 工具组折叠（positions） ----------

    /// <summary>一个 position：多个工具折叠成一个触发按钮，入口是组内最后一个。</summary>
    private sealed class ToolGroup
    {
        public required Button Trigger { get; init; }
        public required Path Icon { get; init; }
        public required AnnotationTool[] Members { get; set; }
        public int EntryIndex { get; set; }
    }

    private readonly List<ToolGroup> _toolGroups = new();
    private DispatcherTimer? _groupShowTimer;
    private DispatcherTimer? _groupCloseTimer;
    private ToolGroup? _pendingGroup;
    private ToolGroup? _activeGroup;

    /// <summary>选项条展开期间被临时摘掉的触发按钮 tooltip。</summary>
    private object? _suppressedTriggerTip;

    /// <summary>当前选项条在窗口里的矩形；命中判断用它，不依赖布局是否已经跑过。</summary>
    private Rect _toolGroupPopoverRect;

    /// <summary>选项条对应的触发按钮矩形（窗口坐标），用于"离开就收起"的位置判断。</summary>
    private Rect _toolGroupTriggerRect;
    private int _groupShowDelayMs = 120;
    private ToolbarLayout _layout = ToolbarLayout.Default();

    /// <summary>测试可调；0 = 立即显示。</summary>
    internal int ToolGroupShowDelayMs
    {
        get => _groupShowDelayMs;
        set => _groupShowDelayMs = Math.Max(0, value);
    }

    /// <summary>
    /// 工具元数据：按钮、图标资源键、线宽补偿、显示名。
    /// 新增工具只在这里登记一次，按钮选中态、折叠组图标、菜单标题都从这里取。
    /// </summary>
    private sealed record ToolEntry(
        AnnotationTool Tool, Button Button, string IconKey, double IconThickness, string Name);

    private readonly Dictionary<AnnotationTool, ToolEntry> _tools = [];

    private void BuildToolRegistry()
    {
        // 补偿值必须和 XAML 里那个按钮写的一致
        Register(AnnotationTool.Rectangle, RectToolButton, "ToolRect", 1.2, "矩形");
        Register(AnnotationTool.Ellipse, EllipseToolButton, "ToolEllipse", 1.2, "椭圆");
        Register(AnnotationTool.Diamond, DiamondToolButton, "ToolDiamond", 1.2, "菱形");
        Register(AnnotationTool.Arrow, ArrowToolButton, "ToolArrow", 1.053, "箭头");
        Register(AnnotationTool.Line, LineToolButton, "ToolLine", 1.05, "直线");
        Register(AnnotationTool.Distance, DistanceToolButton, "ToolDistance", 1.2, "测距");
        Register(AnnotationTool.Angle, AngleToolButton, "ToolAngle", 1.2, "角度");
        Register(AnnotationTool.Bracket, BracketToolButton, "ToolBracket", 1.2, "括号");
        Register(AnnotationTool.Bubble, BubbleToolButton, "ToolBubble", 1.2, "气泡");
        Register(AnnotationTool.Pen, PenToolButton, "ToolPen", 1.125, "画笔");
        Register(AnnotationTool.Highlighter, HighlighterToolButton, "ToolHighlighter", 1.161, "高亮");
        Register(AnnotationTool.Mosaic, MosaicToolButton, "ToolMosaic", 1.2, "马赛克");
        Register(AnnotationTool.Eraser, EraserToolButton, "ToolEraser", 1.2, "橡皮擦");
        Register(AnnotationTool.Spotlight, SpotlightToolButton, "ToolSpotlight", 1.2, "聚光灯");
        Register(AnnotationTool.Magnifier, MagnifierToolButton, "ToolMagnifier", 1.2, "放大镜");
        Register(AnnotationTool.Watermark, WatermarkToolButton, "ToolWatermark", 1.2, "水印");
        Register(AnnotationTool.Text, TextToolButton, "ToolText", 0.972, "文字");
        Register(AnnotationTool.Number, NumberToolButton, "ToolNumber", 1.2, "序号");
    }

    private void Register(AnnotationTool tool, Button button, string iconKey,
        double iconThickness, string name) =>
        _tools[tool] = new ToolEntry(tool, button, iconKey, iconThickness, name);

    private void BuildToolGroups()
    {
        // 成员顺序来自 ToolbarLayout.Groups（唯一事实来源），入口固定在槽位最后一项
        (Button Trigger, Path Icon)[] slots =
        [
            (ShapeGroupButton, ShapeGroupIcon),
            (ArrowGroupButton, ArrowGroupIcon),
            (PenGroupButton, PenGroupIcon),
            (TextGroupButton, TextGroupIcon)
        ];

        for (var index = 0; index < slots.Length; index++)
        {
            var members = ToolbarLayout.Groups[index];
            _toolGroups.Add(new ToolGroup
            {
                Trigger = slots[index].Trigger,
                Icon = slots[index].Icon,
                Members = members,
                EntryIndex = members.Length - 1
            });
        }

        foreach (var group in _toolGroups)
        {
            group.Trigger.Click += (_, _) =>
            {
                HideToolGroupPopover();
                ForceTool(group.Members[group.EntryIndex]);
            };
            group.Trigger.PointerEntered += (_, _) => ScheduleShowToolGroupPopover(group);

            // 离开触发按钮只取消"待展开"；是否收起交给 PointerMoved 的位置判断
            group.Trigger.PointerExited += (_, _) => CancelPendingToolGroupPopover();
            group.Trigger.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Down)
                    return;

                ShowToolGroupPopover(group);
                if (ToolGroupOptions.Children.Count > 0
                    && ToolGroupOptions.Children[0] is Button entry)
                {
                    entry.Focus();
                }

                e.Handled = true;
            };
        }

        ToolGroupPopover.PointerEntered += (_, _) => CancelPendingToolGroupPopover();
        ToolGroupPopover.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            var group = _activeGroup;
            HideToolGroupPopover();
            group?.Trigger.Focus();
            e.Handled = true;
        };
        ToolGroupPopover.LostFocus += (_, _) =>
        {
            if (!ToolGroupPopover.IsKeyboardFocusWithin)
                HideToolGroupPopover();
        };
    }

    private void ScheduleShowToolGroupPopover(ToolGroup group)
    {
        _groupShowTimer?.Stop();
        _pendingGroup = group;

        if (_groupShowDelayMs <= 0)
        {
            ShowToolGroupPopover(group);
            return;
        }

        _groupShowTimer ??= new DispatcherTimer();
        _groupShowTimer.Interval = TimeSpan.FromMilliseconds(_groupShowDelayMs);
        _groupShowTimer.Tick -= OnGroupShowTimerTick;
        _groupShowTimer.Tick += OnGroupShowTimerTick;
        _groupShowTimer.Start();
    }

    private void OnGroupShowTimerTick(object? sender, EventArgs e)
    {
        _groupShowTimer?.Stop();

        if (_pendingGroup is { } group)
            ShowToolGroupPopover(group);
    }

    private void CancelPendingToolGroupPopover()
    {
        _groupShowTimer?.Stop();
        _pendingGroup = null;
    }

    /// <summary>按当前布局重建工具区：单成员显示普通按钮，多成员显示折叠组。</summary>
    private void RenderToolbar()
    {
        ToolButtons.Children.Clear();

        foreach (var position in _layout.Positions)
        {
            var tools = position.Where(_layout.IsVisible).ToList();
            if (tools.Count == 0)
                continue;

            if (tools.Count == 1)
            {
                if (ToolButton(tools[0]) is { } single)
                {
                    single.IsVisible = true;
                    ToolButtons.Children.Add(single);
                }

                continue;
            }

            var groupIndex = ToolbarLayout.GroupIndexFor(tools[0]);
            var sameGroup = groupIndex >= 0
                && tools.All(tool => ToolbarLayout.GroupIndexFor(tool) == groupIndex);

            if (sameGroup)
            {
                var group = _toolGroups[groupIndex];
                group.Members = tools.ToArray();
                group.EntryIndex = group.Members.Length - 1;
                group.Trigger.IsVisible = true;
                ToolTip.SetTip(
                    group.Trigger,
                    $"{ToolbarLayout.GroupLabel(groupIndex)}：{string.Join(" / ", tools.Select(ToolName))}");
                ToolButtons.Children.Add(group.Trigger);
            }
            else
            {
                // 兜底：跨组的手改配置拆成独立按钮
                foreach (var tool in tools)
                {
                    if (ToolButton(tool) is not { } button)
                        continue;

                    button.IsVisible = true;
                    ToolButtons.Children.Add(button);
                }
            }
        }

        foreach (var tool in Enum.GetValues<AnnotationTool>())
        {
            if (ToolButton(tool) is { } button && !ToolButtons.Children.Contains(button))
                button.IsVisible = false;
        }

        foreach (var group in _toolGroups)
        {
            if (!ToolButtons.Children.Contains(group.Trigger))
                group.Trigger.IsVisible = false;
        }

        UpdateToolButtons();
    }

    private void ApplyToolbarLayout(ToolbarLayout layout)
    {
        _layout = layout;
        RenderToolbar();
    }

    /// <summary>测试和编辑器共用：把工具设成所在折叠组的入口。</summary>
    internal void SetToolEntry(AnnotationTool tool)
    {
        _layout.SetEntry(tool);
        RenderToolbar();
    }

    private Button? ToolButton(AnnotationTool tool) =>
        _tools.TryGetValue(tool, out var entry) ? entry.Button : null;

    /// <summary>右键拖拽把手：工具显示、组入口、分组顺序、恢复默认。</summary>
    private void ShowToolLayoutMenu()
    {
        var menu = new ContextMenu();

        var visibility = new MenuItem { Header = "显示工具" };
        foreach (var tool in ToolbarLayout.AllTools())
        {
            var item = new MenuItem
            {
                Header = ToolName(tool),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _layout.IsVisible(tool)
            };

            item.Click += (_, _) =>
            {
                _layout.Toggle(tool);
                RenderToolbar();
            };

            visibility.Items.Add(item);
        }

        menu.Items.Add(visibility);

        var entries = new MenuItem { Header = "组入口" };
        for (var groupIndex = 0; groupIndex < ToolbarLayout.Groups.Length; groupIndex++)
        {
            var group = new MenuItem { Header = ToolbarLayout.GroupLabel(groupIndex) };

            foreach (var tool in ToolbarLayout.Groups[groupIndex])
            {
                var position = _layout.Positions.FirstOrDefault(item => item.Contains(tool));
                var item = new MenuItem
                {
                    Header = ToolName(tool),
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = position is not null && position[^1] == tool
                };

                item.Click += (_, _) =>
                {
                    _layout.SetEntry(tool);
                    RenderToolbar();
                };

                group.Items.Add(item);
            }

            entries.Items.Add(group);
        }

        menu.Items.Add(entries);

        var order = new MenuItem { Header = "分组顺序" };
        for (var index = 0; index < _layout.Positions.Count; index++)
        {
            var position = _layout.Positions[index];
            if (position.Count == 0)
                continue;

            var groupIndex = ToolbarLayout.GroupIndexFor(position[0]);
            var label = groupIndex >= 0
                && position.All(tool => ToolbarLayout.GroupIndexFor(tool) == groupIndex)
                    ? ToolbarLayout.GroupLabel(groupIndex)
                    : ToolName(position[0]);

            var entry = new MenuItem { Header = label };
            var up = new MenuItem { Header = "上移" };
            var down = new MenuItem { Header = "下移" };
            var captured = index;

            up.Click += (_, _) =>
            {
                _layout.MovePosition(captured, -1);
                RenderToolbar();
            };
            down.Click += (_, _) =>
            {
                _layout.MovePosition(captured, +1);
                RenderToolbar();
            };

            entry.Items.Add(up);
            entry.Items.Add(down);
            order.Items.Add(entry);
        }

        menu.Items.Add(order);
        menu.Items.Add(new Separator());

        var reset = new MenuItem { Header = "恢复默认" };
        reset.Click += (_, _) => ApplyToolbarLayout(ToolbarLayout.Default());
        menu.Items.Add(reset);

        menu.Open(ToolbarDragHandle);
    }

    // ---------- 更多识别 ----------

    private void OnMoreRecognitionClick(object? sender, RoutedEventArgs e)
    {
        if (RecognitionPopover.IsVisible)
        {
            HideRecognitionPopover();
            return;
        }

        ShowRecognitionPopover();
    }

    /// <summary>更多识别选项条：摆在触发按钮正上方，和工具组弹层同一套样式。</summary>
    private void ShowRecognitionPopover()
    {
        RecognitionPopover.IsVisible = true;

        const double width = 3 * 32 + 2 * 2 + 10;
        const double height = 32 + 10;

        var origin = MoreRecognitionButton.TranslatePoint(new Point(0, 0), ToolbarCoordinateRoot) ?? new Point(0, 0);
        var left = Math.Clamp(origin.X, 8, Math.Max(8, Width - width - 8));
        var top = Math.Max(8, origin.Y - height - 6);

        Canvas.SetLeft(RecognitionPopover, left);
        Canvas.SetTop(RecognitionPopover, top);
    }

    private void HideRecognitionPopover() => RecognitionPopover.IsVisible = false;

    private void OnRecognizeTextOption(object? sender, RoutedEventArgs e)
    {
        HideRecognitionPopover();
        _ = RunRecognitionAsync("text");
    }

    private void OnRecognizeTableHtmlOption(object? sender, RoutedEventArgs e)
    {
        HideRecognitionPopover();
        _ = RunRecognitionAsync("table-html");
    }

    private void OnRecognizeTableMarkdownOption(object? sender, RoutedEventArgs e)
    {
        HideRecognitionPopover();
        _ = RunRecognitionAsync("table-md");
    }

    /// <summary>展开某个工具的选项条，摆在触发按钮正上方。</summary>
    private void ShowToolGroupPopover(ToolGroup group)
    {
        _groupShowTimer?.Stop();
        _pendingGroup = null;

        // 同一组已经展开：不要重建子元素，否则会把正在按下的按钮换掉，点击丢失
        if (ToolGroupPopover.IsVisible && ReferenceEquals(_activeGroup, group))
            return;

        _activeGroup = group;

        // 触发按钮的 tooltip 会弹在选项条上面挡住点击，展开期间先摘掉
        SuppressTriggerToolTip(group.Trigger);
        ToolGroupOptions.Children.Clear();

        // 反序：入口在最左（参考 stackPresentation 把 popoverItemIds 反了过来）
        for (var i = group.Members.Length - 1; i >= 0; i--)
        {
            var tool = group.Members[i];
            var option = new Button { Focusable = true };
            option.Classes.Add("tool");

            var icon = new Path();
            ApplyToolIcon(icon, tool);
            option.Content = icon;
            ToolTip.SetTip(option, ToolName(tool));
            SetSelected(option, tool == group.Members[group.EntryIndex]);

            option.Click += (_, _) =>
            {
                group.EntryIndex = Array.IndexOf(group.Members, tool);
                HideToolGroupPopover();
                ForceTool(tool);
            };

            ToolGroupOptions.Children.Add(option);
        }

        ToolGroupPopover.IsVisible = true;

        // 尺寸按内容算：32 的按钮 + 2 的间距 + Border 的 padding 4×2 与边框 1×2
        var width = group.Members.Length * 32 + (group.Members.Length - 1) * 2 + 10;
        var height = 32 + 10;

        var origin = group.Trigger.TranslatePoint(new Point(0, 0), ToolbarCoordinateRoot) ?? new Point(0, 0);
        // 与触发按钮左对齐：比居中更稳定，也不会因为按钮宽度取不到而左右跳
        var left = Math.Clamp(origin.X, 8, Math.Max(8, Width - width - 8));
        var top = Math.Max(8, origin.Y - height - 6);

        var triggerSize = group.Trigger.Bounds.Size;
        _toolGroupTriggerRect = new Rect(
            origin,
            triggerSize.Width > 0 && triggerSize.Height > 0 ? triggerSize : new Size(32, 32));

        Canvas.SetLeft(ToolGroupPopover, left);
        Canvas.SetTop(ToolGroupPopover, top);
        _toolGroupPopoverRect = new Rect(left, top, width, height);
    }

    /// <summary>
    /// 离开悬停区后延迟收起：到点再按当前光标复核一次，
    /// 指针其实已经进到选项条里就不收（对齐 snow-shot 的 finishHoverClose(recheck)）。
    /// </summary>
    private void ScheduleCloseToolGroupPopover()
    {
        if (_groupCloseTimer is null)
        {
            _groupCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _groupCloseTimer.Tick += OnGroupCloseTimerTick;
        }

        _groupCloseTimer.Stop();
        _groupCloseTimer.Start();
    }

    private void OnGroupCloseTimerTick(object? sender, EventArgs e)
    {
        _groupCloseTimer?.Stop();

        if (!ToolGroupPopover.IsVisible || _activeGroup is null)
            return;

        var hoverArea = _toolGroupPopoverRect.Union(_toolGroupTriggerRect).Inflate(10);

        if (!hoverArea.Contains(_lastPointerPoint))
            HideToolGroupPopover();
    }

    private void HideToolGroupPopover()
    {
        _groupShowTimer?.Stop();
        _groupCloseTimer?.Stop();
        _pendingGroup = null;

        if (_activeGroup is { } group)
            RestoreTriggerToolTip(group.Trigger);

        _activeGroup = null;
        ToolGroupPopover.IsVisible = false;
        _toolGroupPopoverRect = default;
        _toolGroupTriggerRect = default;
        ToolGroupOptions.Children.Clear();
    }

    /// <summary>展开选项条期间摘掉触发按钮的 tooltip，避免它弹在选项条上吃掉点击。</summary>
    private void SuppressTriggerToolTip(Button trigger)
    {
        _suppressedTriggerTip = ToolTip.GetTip(trigger);
        ToolTip.SetIsOpen(trigger, false);
        ToolTip.SetTip(trigger, null);
    }

    private void RestoreTriggerToolTip(Button trigger)
    {
        if (_suppressedTriggerTip is not null)
            ToolTip.SetTip(trigger, _suppressedTriggerTip);

        _suppressedTriggerTip = null;
    }

    private void ApplyToolIcon(Path icon, AnnotationTool tool)
    {
        if (!_tools.TryGetValue(tool, out var info))
            return;

        if (this.TryFindResource(info.IconKey, out var resource) && resource is Geometry geometry)
        {
            icon.Data = geometry;
            icon.StrokeThickness = info.IconThickness;
        }
    }

    private string ToolName(AnnotationTool tool) =>
        _tools.TryGetValue(tool, out var entry) ? entry.Name : string.Empty;

    private void UpdateToolButtons()
    {
        InvalidateToolbarMetrics();
        SetSelected(SelectToolButton, _tool == AnnotationTool.None);

        // 所有工具按钮的选中态统一走注册表，新增工具不用再改这里
        foreach (var entry in _tools.Values)
            SetSelected(entry.Button, entry.Tool == _tool);

        UndoButton.IsEnabled = _notes.CanUndo || _selectionUndo.Count > 0;
        RedoButton.IsEnabled = _notes.CanRedo || _selectionRedo.Count > 0;

        // 折叠组：入口跟着当前工具走，按钮图标显示组里正在用的那个
        foreach (var group in _toolGroups)
        {
            var active = Array.IndexOf(group.Members, _tool);
            if (active >= 0)
                group.EntryIndex = active;

            ApplyToolIcon(group.Icon, active >= 0 ? _tool : group.Members[group.EntryIndex]);
            SetSelected(group.Trigger, active >= 0);
        }

        // 属性行只在选中标注工具时出现；橡皮擦/放大镜没有可调属性，整条让出去
        var styleTool = ActiveStyleTool;
        StyleRow.IsVisible = styleTool is not (
            AnnotationTool.None or AnnotationTool.Eraser or AnnotationTool.Magnifier);

        // 形状类型组：跟随形状折叠组的成员，选中态由 UpdateStyleButtons 统一刷新
        ShapeTypeGroup.IsVisible = ToolbarLayout.GroupIndexFor(styleTool) == 0;

        // 每组属性只在当前工具 / 选中图形用得上时出现（LumaShot 的属性卡思路）
        var drawing = styleTool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
            or AnnotationTool.Diamond
            or AnnotationTool.Arrow or AnnotationTool.Line or AnnotationTool.Distance
            or AnnotationTool.Angle or AnnotationTool.Bracket or AnnotationTool.Bubble
            or AnnotationTool.Pen or AnnotationTool.Highlighter;

        ColorGroup.IsVisible = styleTool is not (
            AnnotationTool.None or AnnotationTool.Mosaic or AnnotationTool.Spotlight
            or AnnotationTool.Eraser);
        WidthGroup.IsVisible = drawing;
        DashGroup.IsVisible = SupportsDash(styleTool);
        FillGroup.IsVisible = styleTool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
            or AnnotationTool.Diamond or AnnotationTool.Bubble;
        OpacityGroup.IsVisible = (drawing && styleTool is not (AnnotationTool.Bubble or AnnotationTool.Angle))
            || styleTool is AnnotationTool.Spotlight or AnnotationTool.Watermark;

        RadiusGroup.IsVisible = styleTool == AnnotationTool.Rectangle;

        // 水印和文字共用字号档位；加粗只对文字有意义
        FontGroup.IsVisible = styleTool is AnnotationTool.Text or AnnotationTool.Watermark
            or AnnotationTool.Bubble;
        BoldButton.IsVisible = styleTool == AnnotationTool.Text;
        FontFamilyBox.IsVisible = styleTool == AnnotationTool.Text;
        TextLayoutGroup.IsVisible = styleTool == AnnotationTool.Text;
        WatermarkGroup.IsVisible = styleTool == AnnotationTool.Watermark;
        NumberGroup.IsVisible = styleTool == AnnotationTool.Number;
        NumberFormatGroup.IsVisible = styleTool == AnnotationTool.Number;
        MosaicGroup.IsVisible = styleTool == AnnotationTool.Mosaic;

        // 选中态和读数统一交给 UpdateStyleButtons，避免和样式变更路径各写一份
        UpdateStyleButtons();

        // 属性行显隐会改变工具条宽度：先让它重新测量，再重新居中（用户拖过就不再跟随）
        ActionBar.InvalidateMeasure();
        MeasureActionBar();

        if (ActionBar.IsVisible && !_toolbarManual)
            PositionActionBar(_selection);

        // 工具切换会改变「选区是否可交互」：录制中立刻同步窗口区域。
        UpdateRecordingInputRegion();
    }

    private static void SetSelected(Button button, bool selected)
    {
        if (selected)
        {
            if (!button.Classes.Contains("selected"))
                button.Classes.Add("selected");
        }
        else
        {
            button.Classes.Remove("selected");
        }
    }
}
