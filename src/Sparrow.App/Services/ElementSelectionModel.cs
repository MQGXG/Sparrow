using Avalonia;

namespace Sparrow.App.Services;

/// <summary>
/// 智能选区的目标形态：整窗，或窗口里的子元素。
/// 和 Snow Shot 的 SmartSelectionMode 对应（Disabled 由 CaptureElementApi 控制）。
/// </summary>
public enum ElementSelectionTarget
{
    Window,
    WindowSubElement
}

/// <summary>
/// 元素级智能选区的路径状态。
///
/// 探测返回的路径是「深 → 浅」排列，最后一项一定是窗口矩形。本类型只负责：
/// - 裁剪到可选区域、丢掉重复/过小的层；
/// - 决定当前选哪一层（整窗模式固定末项，元素模式默认最深）；
/// - 细化时保证新路径是在旧路径上原地向外扩展，不把用户选中的层弄丢。
///
/// 形状与 Snow Shot 的 <c>ScreenshotIntelligentSelectionModel</c> 一致，纯数据、可单测。
/// </summary>
public sealed class ElementSelectionModel
{
    private readonly List<UiElementCandidate> _path = [];
    private int _index = -1;

    /// <summary>用户是否用滚轮/接口显式选过层；细化时按矩形保留这一层。</summary>
    private bool _explicit;

    public ElementSelectionTarget Target { get; private set; } = ElementSelectionTarget.WindowSubElement;

    public IReadOnlyList<UiElementCandidate> Path => _path;

    public int Index => _index;

    public bool HasSelection => _index >= 0 && _index < _path.Count;

    public UiElementCandidate? Current => HasSelection ? _path[_index] : null;

    /// <summary>路径末项即外层窗口；没有路径时为 null。</summary>
    public UiElementCandidate? Window => _path.Count > 0 ? _path[^1] : null;

    public void Reset()
    {
        _path.Clear();
        _index = -1;
        _explicit = false;
        Target = ElementSelectionTarget.WindowSubElement;
    }

    /// <summary>切换整窗 / 子元素。切到整窗时固定选末项。</summary>
    public bool ToggleTarget()
    {
        Target = Target == ElementSelectionTarget.Window
            ? ElementSelectionTarget.WindowSubElement
            : ElementSelectionTarget.Window;

        _explicit = false;
        _index = _path.Count == 0
            ? -1
            : Target == ElementSelectionTarget.Window ? _path.Count - 1 : 0;

        return _path.Count > 0;
    }

    /// <summary>应用一次新的命中路径。相同路径不重置用户已选的层。</summary>
    public bool ApplyPath(
        IReadOnlyList<UiElementCandidate> path,
        PixelRect selectableBounds,
        int minimumSize)
    {
        var normalized = Normalize(path, selectableBounds, minimumSize);

        if (normalized.Count == 0)
        {
            var had = _path.Count > 0;
            ResetPath();
            return had;
        }

        if (SamePath(normalized, _path))
            return false;

        // 用户显式选过层，且新路径仍包含旧路径：保留同一块矩形，别让细化把层重置
        var selected = Current;
        var keepExplicit = _explicit
                           && selected is not null
                           && ExtendsCurrent(normalized, _path);

        _path.Clear();
        _path.AddRange(normalized);

        if (keepExplicit)
        {
            var kept = _path.FindIndex(candidate => candidate.Bounds.Equals(selected!.Bounds));
            _index = kept >= 0 ? kept : 0;
            return true;
        }

        _explicit = false;
        _index = Target == ElementSelectionTarget.Window ? _path.Count - 1 : 0;
        return true;
    }

    /// <summary>
    /// 应用细化路径。只有在新路径「完整包含旧路径作为尾部」时才接受：
    /// 细化只会从更深的元素向外补齐，绝不会把框跳到另一棵子树。
    /// </summary>
    public bool ApplyRefinement(
        IReadOnlyList<UiElementCandidate> path,
        PixelRect selectableBounds,
        int minimumSize)
    {
        if (Target != ElementSelectionTarget.WindowSubElement || _path.Count == 0)
            return false;

        var normalized = Normalize(path, selectableBounds, minimumSize);

        if (!ExtendsCurrent(normalized, _path))
            return false;

        var selected = Current;
        var explicitSelection = _explicit;

        _path.Clear();
        _path.AddRange(normalized);

        if (explicitSelection && selected is not null)
        {
            var kept = _path.FindIndex(candidate => candidate.Bounds.Equals(selected.Bounds));
            _index = kept >= 0 ? kept : 0;
        }
        else
        {
            _index = 0;
        }

        return true;
    }

    /// <summary>
    /// 细化路径不满足“旧层都在”时，是否允许按替换处理。
    /// 典型场景：UIA 从顶层窗口切到 Chrome 渲染子窗口，旧路径里的顶层 Document
    /// 不会再出现在子窗口路径里；只要仍是同一窗口、且新路径更深，就允许替换。
    /// 用户显式选过层时不替换，避免抢走用户的选择。
    /// </summary>
    public bool CanReplaceWith(
        IReadOnlyList<UiElementCandidate> path,
        PixelRect selectableBounds,
        int minimumSize)
    {
        if (_explicit || _path.Count == 0)
            return false;

        var normalized = Normalize(path, selectableBounds, minimumSize);

        if (normalized.Count == 0)
            return false;

        if (!normalized[^1].Bounds.Equals(_path[^1].Bounds))
            return false;

        var newArea = (long)normalized[0].Bounds.Width * normalized[0].Bounds.Height;
        var oldArea = (long)_path[0].Bounds.Width * _path[0].Bounds.Height;

        return newArea < oldArea || normalized.Count > _path.Count;
    }

    /// <summary>显式选层（滚轮 / 外部调用）。整窗模式下强制末项。</summary>
    public bool SelectIndex(int index)
    {
        _explicit = true;
        return SetIndex(index);
    }

    /// <summary>在路径内按 delta 逐级切换；整窗模式忽略。</summary>
    public bool Step(int delta)
    {
        if (Target != ElementSelectionTarget.WindowSubElement || !HasSelection)
            return false;

        _explicit = true;
        return SetIndex(_index + delta);
    }

    private bool SetIndex(int index)
    {
        if (_path.Count == 0)
        {
            _index = -1;
            return false;
        }

        _index = Target == ElementSelectionTarget.Window
            ? _path.Count - 1
            : Math.Clamp(index, 0, _path.Count - 1);

        return true;
    }

    private void ResetPath()
    {
        _path.Clear();
        _index = -1;
        _explicit = false;
    }

    private static bool SamePath(
        IReadOnlyList<UiElementCandidate> left,
        IReadOnlyList<UiElementCandidate> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Bounds.Equals(right[i].Bounds))
                return false;
        }

        return true;
    }

    /// <summary>
    /// candidate 是否比 current 更深，且完整包含 current 的每一层（按序）、末项窗口相同。
    /// 允许新树在旧层之间插入新的层级（UIA 树在细化时经常这样），
    /// 但不允许旧层消失或换到另一棵子树。
    /// </summary>
    private static bool ExtendsCurrent(
        IReadOnlyList<UiElementCandidate> candidate,
        IReadOnlyList<UiElementCandidate> current)
    {
        if (candidate.Count <= current.Count)
            return false;

        if (current.Count > 0 && !candidate[^1].Bounds.Equals(current[^1].Bounds))
            return false;

        var index = 0;

        foreach (var item in candidate)
        {
            if (index < current.Count && item.Bounds.Equals(current[index].Bounds))
                index++;
        }

        return index == current.Count;
    }

    /// <summary>
    /// 裁剪到可选区域、去重、丢掉小于最小尺寸的层。
    /// 路径保持「深 → 浅」原始顺序。
    /// </summary>
    private static List<UiElementCandidate> Normalize(
        IReadOnlyList<UiElementCandidate> path,
        PixelRect bounds,
        int minimumSize)
    {
        var result = new List<UiElementCandidate>(path.Count);

        foreach (var candidate in path)
        {
            var clipped = Intersect(candidate.Bounds, bounds);

            if (clipped.Width < minimumSize || clipped.Height < minimumSize)
                continue;

            if (result.Count > 0 && result[^1].Bounds.Equals(clipped))
                continue;

            result.Add(candidate with { Bounds = clipped });
        }

        return result;
    }

    private static PixelRect Intersect(PixelRect left, PixelRect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);

        return rightEdge <= x || bottomEdge <= y
            ? default
            : new PixelRect(x, y, rightEdge - x, bottomEdge - y);
    }
}
