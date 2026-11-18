using System.Runtime.InteropServices;
using Avalonia;
using Interop.UIAutomationClient;

namespace Sparrow.App.Services;

/// <summary>UI Automation 探测到的一个控件候选。</summary>
public sealed record UiElementCandidate(
    PixelRect Bounds,
    string Title,
    string ControlType);

/// <summary>
/// 按窗口句柄遍历 UI Automation 子树，找光标下最深的控件。
///
/// 为什么不直接 <c>AutomationElement.FromPoint</c>：截图遮罩在最上层，
/// 屏幕命中只会拿到遮罩自己。按句柄进目标窗口的树，用矩形命中，
/// 既不依赖屏幕命中，也不需要临时隐藏遮罩。
///
/// 兼容性：Win32/WPF/WinForms/UWP 通常没问题；Electron/自绘界面可能只暴露
/// 窗口级，探测不到时退回窗口候选。
/// </summary>
public static class UiElementProbe
{
    private const int MaxDepth = 14;
    private const int MaxNodes = 260;

    /// <summary>控件类型的中文名；LocalizedControlType 在部分应用里是空串。</summary>
    private static readonly Dictionary<int, string> ControlTypeNames = new()
    {
        [UIA_ControlTypeIds.UIA_ButtonControlTypeId] = "按钮",
        [UIA_ControlTypeIds.UIA_CheckBoxControlTypeId] = "复选框",
        [UIA_ControlTypeIds.UIA_ComboBoxControlTypeId] = "下拉框",
        [UIA_ControlTypeIds.UIA_EditControlTypeId] = "输入框",
        [UIA_ControlTypeIds.UIA_HyperlinkControlTypeId] = "链接",
        [UIA_ControlTypeIds.UIA_ImageControlTypeId] = "图片",
        [UIA_ControlTypeIds.UIA_ListItemControlTypeId] = "列表项",
        [UIA_ControlTypeIds.UIA_MenuItemControlTypeId] = "菜单项",
        [UIA_ControlTypeIds.UIA_RadioButtonControlTypeId] = "单选项",
        [UIA_ControlTypeIds.UIA_SliderControlTypeId] = "滑块",
        [UIA_ControlTypeIds.UIA_ProgressBarControlTypeId] = "进度条",
        [UIA_ControlTypeIds.UIA_TabItemControlTypeId] = "选项卡",
        [UIA_ControlTypeIds.UIA_TreeItemControlTypeId] = "树节点",
        [UIA_ControlTypeIds.UIA_TextControlTypeId] = "文本",
        [UIA_ControlTypeIds.UIA_ToolBarControlTypeId] = "工具栏",
        [UIA_ControlTypeIds.UIA_DataItemControlTypeId] = "数据项",
        [UIA_ControlTypeIds.UIA_DataGridControlTypeId] = "表格",
        [UIA_ControlTypeIds.UIA_TableControlTypeId] = "表格",
        [UIA_ControlTypeIds.UIA_HeaderItemControlTypeId] = "表头",
        [UIA_ControlTypeIds.UIA_SplitButtonControlTypeId] = "下拉按钮",
        [UIA_ControlTypeIds.UIA_DocumentControlTypeId] = "文档",
        [UIA_ControlTypeIds.UIA_PaneControlTypeId] = "窗格",
        [UIA_ControlTypeIds.UIA_GroupControlTypeId] = "分组",
        [UIA_ControlTypeIds.UIA_CustomControlTypeId] = "控件"
    };

    /// <summary>纯布局容器：没有名字时不当候选，否则整块区域都会被框住。</summary>
    private static readonly HashSet<int> LayoutControlTypes =
    [
        UIA_ControlTypeIds.UIA_PaneControlTypeId,
        UIA_ControlTypeIds.UIA_GroupControlTypeId,
        UIA_ControlTypeIds.UIA_CustomControlTypeId
    ];

    public static UiElementCandidate? Probe(IntPtr hwnd, PixelPoint point)
    {
        IUIAutomation? automation = null;

        try
        {
            automation = (IUIAutomation)new CUIAutomation();
            var root = automation.ElementFromHandle(hwnd);
            if (root is null)
                return null;

            var budget = MaxNodes;
            var best = Walk(automation.ControlViewWalker, root, point, depth: 0, ref budget);

            if (best is null)
                return null;

            // 整个窗口没有更细的控件：交给窗口候选，避免重复框同一块
            var windowRect = root.CurrentBoundingRectangle;
            var windowArea = Math.Max(
                1L, (long)(windowRect.right - windowRect.left) * (windowRect.bottom - windowRect.top));
            var bestArea = (long)best.Bounds.Width * best.Bounds.Height;

            return bestArea >= windowArea * 95 / 100 ? null : best;
        }
        catch
        {
            // 目标应用没有 UIA 树、COM 失败、窗口已关闭等：一律退回窗口级
            return null;
        }
        finally
        {
            if (automation is not null)
                Marshal.FinalReleaseComObject(automation);
        }
    }

    private static UiElementCandidate? Walk(
        IUIAutomationTreeWalker walker,
        IUIAutomationElement element,
        PixelPoint point,
        int depth,
        ref int budget)
    {
        if (depth > MaxDepth || budget-- <= 0)
            return null;

        tagRECT rect;
        int controlTypeId;

        try
        {
            if (element.CurrentIsOffscreen != 0)
                return null;

            rect = element.CurrentBoundingRectangle;
            controlTypeId = element.CurrentControlType;
        }
        catch
        {
            return null;
        }

        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        if (width < 4 || height < 4 || !Contains(rect, point))
            return null;

        var bounds = new PixelRect(rect.left, rect.top, width, height);
        var controlType = ControlTypeName(controlTypeId);
        var name = ElementName(element);

        var best = IsSelectable(controlTypeId, name)
            ? new UiElementCandidate(bounds, string.IsNullOrWhiteSpace(name) ? controlType : name, controlType)
            : null;

        IUIAutomationElement? child;

        try
        {
            child = walker.GetFirstChildElement(element);
        }
        catch
        {
            return best;
        }

        while (child is not null && budget > 0)
        {
            var hit = Walk(walker, child, point, depth + 1, ref budget);

            if (hit is not null &&
                (long)hit.Bounds.Width * hit.Bounds.Height < (long)bounds.Width * bounds.Height)
            {
                best = hit;
            }

            try
            {
                child = walker.GetNextSiblingElement(child);
            }
            catch
            {
                break;
            }
        }

        return best;
    }

    /// <summary>窗口本身和无名布局容器不算控件候选。</summary>
    private static bool IsSelectable(int controlTypeId, string? name) =>
        controlTypeId != UIA_ControlTypeIds.UIA_WindowControlTypeId &&
        !(LayoutControlTypes.Contains(controlTypeId) && string.IsNullOrWhiteSpace(name));

    private static string? ElementName(IUIAutomationElement element)
    {
        try
        {
            return element.CurrentName;
        }
        catch
        {
            return null;
        }
    }

    private static string ControlTypeName(int controlTypeId)
    {
        if (ControlTypeNames.TryGetValue(controlTypeId, out var name))
            return name;

        return "控件";
    }

    private static bool Contains(tagRECT rect, PixelPoint point) =>
        point.X >= rect.left && point.X < rect.right &&
        point.Y >= rect.top && point.Y < rect.bottom;
}
