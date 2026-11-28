using Avalonia.Threading;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Overlays;

/// <summary>
/// 宿主对 <see cref="IWindowOverlay"/> 的实现：所有覆盖窗都在宿主创建，
/// 插件只描述「要拾取窗口 / 要显示图钉」，保持「插件不创建窗口」的契约。
/// </summary>
public sealed class WindowOverlay : IWindowOverlay, IDisposable
{
    private readonly IAppLog _log;
    private readonly Dictionary<nint, PinIndicatorWindow> _indicators = [];
    private readonly PinWatchdogClient _watchdog;

    public WindowOverlay(IAppLog log)
    {
        _log = log;
        _watchdog = new PinWatchdogClient(log);
    }

    public Task<nint> PickWindowAsync(string hint, CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync<nint>(
            () => new WindowPickerWindow(hint).PickAsync(cancellationToken));

    public void ShowPinIndicator(nint window, Action onRemove) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_indicators.Remove(window, out var existing))
                existing.Close();

            var indicator = new PinIndicatorWindow(window, onRemove);
            _indicators[window] = indicator;

            // 窗口被用户关掉时同步清掉记录，避免下次重复显示
            indicator.Closed += (_, _) =>
            {
                if (_indicators.TryGetValue(window, out var current) && ReferenceEquals(current, indicator))
                    _indicators.Remove(window);

                _watchdog.Unpin(window);
            };

            indicator.Show();

            // 看门狗记录「这个窗口被我们置顶了」：宿主被强杀时由它恢复
            _watchdog.Pin(window);
        });

    public void HidePinIndicator(nint window) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_indicators.Remove(window, out var indicator))
                indicator.Close();

            _watchdog.Unpin(window);
        });

    /// <summary>退出时恢复所有被置顶窗口：不留「钉住但没人管」的状态。</summary>
    public void Dispose()
    {
        foreach (var (window, indicator) in _indicators.ToArray())
        {
            try
            {
                if (WindowPickerApi.Exists(window))
                    WindowPickerApi.SetTopMost(window, false);
            }
            catch (Exception ex)
            {
                _log.Warn($"恢复窗口置顶状态失败：{ex.Message}");
            }

            indicator.Close();
        }

        _indicators.Clear();
        _watchdog.Clear();
        _watchdog.Dispose();
    }
}
