namespace Sparrow.Plugin.Sdk;

/// <summary>
/// 宿主提供的窗口覆盖能力：拾取窗口 + 在被置顶窗口上显示图钉。
///
/// 插件不允许自己创建窗口（见 docs/plugin-model.md 宿主能力边界），
/// 所有需要覆盖层的交互都走这里。无宿主环境（单元测试）可能为 null，插件必须判空。
/// </summary>
public interface IWindowOverlay
{
    /// <summary>
    /// 进入拾取模式：全屏遮罩 + 图钉光标跟随，用户在目标窗口上点击即返回根窗口句柄。
    /// 右键 / Esc / 取消令牌触发时返回 0。
    /// </summary>
    Task<nint> PickWindowAsync(string hint, CancellationToken cancellationToken);

    /// <summary>
    /// 在目标窗口标题栏旁显示图钉；用户点击图钉时回调 <paramref name="onRemove"/>，
    /// 由插件负责真正取消置顶并调用 <see cref="HidePinIndicator"/>。
    /// </summary>
    void ShowPinIndicator(nint window, Action onRemove);

    /// <summary>移除指定窗口的图钉。</summary>
    void HidePinIndicator(nint window);
}
