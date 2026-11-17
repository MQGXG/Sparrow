using Avalonia.Threading;
using Sparrow.App.Services;

namespace Sparrow.App.Overlays;

/// <summary>
/// 统一的轻提示出口：所有「软件内提示信息」都走这里。
/// 同一时间只显示一条，新消息替换旧消息；调用方不需要关心线程。
/// </summary>
public sealed class ToastService : IDisposable
{
    private readonly IAppLog _log;
    private ToastWindow? _current;

    public ToastService(IAppLog log) => _log = log;

    public void Show(string message, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(message, duration));
            return;
        }

        try
        {
            _current?.Close();

            var toast = new ToastWindow(message, duration);
            _current = toast;

            toast.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, toast))
                    _current = null;
            };

            toast.Show();
        }
        catch (Exception ex)
        {
            // 提示失败不能反过来影响主流程
            _log.Warn($"toast 显示失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _current?.Close();
        _current = null;
    }
}
