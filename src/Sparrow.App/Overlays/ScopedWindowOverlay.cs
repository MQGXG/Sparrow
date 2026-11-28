using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Overlays;

/// <summary>
/// 每个插件一个 <see cref="IWindowOverlay"/> 作用域。
///
/// 记录插件创建的图钉，插件卸载时取消置顶并隐藏图钉。
/// 没有这层作用域时，<see cref="WindowOverlay"/> 会一直持有插件传进来的
/// <c>onRemove</c> 回调，插件 ALC 无法回收，窗口也会保持置顶。
/// </summary>
internal sealed class ScopedWindowOverlay : IWindowOverlay, IDisposable
{
    private readonly IWindowOverlay _inner;
    private readonly object _gate = new();
    private readonly HashSet<nint> _pins = [];
    private bool _disposed;

    public ScopedWindowOverlay(IWindowOverlay inner) => _inner = inner;

    public Task<nint> PickWindowAsync(string hint, CancellationToken cancellationToken) =>
        _inner.PickWindowAsync(hint, cancellationToken);

    public void ShowPinIndicator(nint window, Action onRemove)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _pins.Add(window);
        }

        _inner.ShowPinIndicator(window, onRemove);
    }

    public void HidePinIndicator(nint window)
    {
        lock (_gate)
            _pins.Remove(window);

        _inner.HidePinIndicator(window);
    }

    public void Dispose()
    {
        nint[] pins;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            pins = _pins.ToArray();
            _pins.Clear();
        }

        foreach (var window in pins)
        {
            try
            {
                if (WindowPickerApi.Exists(window))
                    WindowPickerApi.SetTopMost(window, false);
            }
            catch
            {
                // 窗口已经销毁 / 句柄失效：继续清理其他图钉
            }

            _inner.HidePinIndicator(window);
        }
    }
}
